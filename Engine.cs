using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    // TODO: subordinate workflows
    //       -- on enter, start new workflow in subordinate
    //          -- initial data... the parent workflow? some retrieval interface?
    //       -- on subordinate exit, step parent state as normal
    //       -- on subordinate error, invoke parent's error policy
    //       -- if subordinate not complete, skip step of parent?
    //       -- subordinates can be run async at start of step
    //       -- CurrentState = "StateWithSubordinate.ChildState.GrandchildState"
    class WorkflowEngine<TWorkflowData> : IWorkflowEngine<TWorkflowData> where TWorkflowData : class
    {
        public Workflow<TWorkflowData> StartWorkflow(TWorkflowData data)
        {
            var workflow = new Workflow<TWorkflowData>(Guid.NewGuid(), InitialState, data);

            _activeWorkflows.Add(workflow);

            return workflow;
        }

        public void Step()
        {
            lock (_stepLock)
            {
                _stepTimer.Restart();
                ++_stepCount;

                TimeSpan runTime;
                if (_runTimer.IsRunning)
                {
                    runTime = _runTimer.Elapsed;
                }
                else
                {
                    runTime = TimeSpan.Zero;
                    _runTimer.Start();
                }

                //Task<IEnumerable<object>> fetchTask;
                //TimeSpan timeout;
                //if (WorkflowDataRetriever != null)
                //{
                //    fetchTask = Task.Run(() => WorkflowDataRetriever.FetchWorkflowData());
                //    timeout = WorkflowDataRetriever.FetchTimeout;
                //}
                //else
                //{
                //    fetchTask = Task.FromResult(Enumerable.Empty<object>());
                //    timeout = TimeSpan.Zero;
                //}

                foreach (var subordinateEngine in Subordinates)
                {
                    if (!subordinateEngine.ActiveWorkflows.Any())
                    {
                        continue;
                    }

                    subordinateEngine.Step();
                }

                var exitedWorkflows = new List<Tuple<Workflow<TWorkflowData>, Exit<TWorkflowData>>>();
                var failedWorkflows = new List<Tuple<Workflow<TWorkflowData>, Exception>>();
                foreach (var workflow in _activeWorkflows)
                {
                    if (_workflowsAwaitingSubordinate.Contains(workflow))
                    {
                        continue;
                    }

                    workflow.OnBeginStep();

                    IState state = workflow.CurrentState;
                    IErrorPolicy<TWorkflowData> errorPolicy = GetErrorPolicy(state.Name);

                    bool wasRemoved = false;
                    var exits = GetExits(state.Name);
                    try
                    {
                        foreach (var exit in exits)
                        {
                            if (EvaluateCondition(exit.Condition, workflow))
                            {
                                exitedWorkflows.Add(Tuple.Create(workflow, exit));
                                wasRemoved = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!errorPolicy.ShouldContinue(workflow.Data, ex))
                        {
                            failedWorkflows.Add(Tuple.Create(workflow, ex));
                            continue;
                        }
                    }

                    if (wasRemoved)
                    {
                        continue;
                    }

                    bool wasTransitioned = false;
                    var transitions = GetTransitions(state.Name);
                    try
                    {
                        foreach (var transition in transitions)
                        {
                            if (EvaluateCondition(transition.Condition, workflow))
                            {
                                workflow.CurrentState = transition.To;
                                OnWorkflowTransitioned(workflow, transition);
                                wasTransitioned = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!errorPolicy.ShouldContinue(workflow.Data, ex))
                        {
                            failedWorkflows.Add(Tuple.Create(workflow, ex));
                            continue;
                        }
                    }

                    if (wasTransitioned)
                    {
                        continue;
                    }

                    var triggers = GetTriggers(state.Name);
                    try
                    {
                        foreach (var trigger in triggers)
                        {
                            bool shouldInvoke = true;

                            if (trigger.IsRateLimited && trigger.TimeLastTriggered.HasValue)
                            {
                                TimeSpan timeSinceLastTrigger = runTime - trigger.TimeLastTriggered.Value;
                                shouldInvoke = timeSinceLastTrigger > trigger.RateLimit.Value;
                            }

                            if (shouldInvoke && EvaluateCondition(trigger.Condition, workflow))
                            {
                                trigger.Action(workflow.Data);
                                trigger.TimeLastTriggered = runTime;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!errorPolicy.ShouldContinue(workflow.Data, ex))
                        {
                            failedWorkflows.Add(Tuple.Create(workflow, ex));
                            continue;
                        }
                    }
                }

                foreach (var pair in exitedWorkflows)
                {
                    Workflow<TWorkflowData> workflow = pair.Item1;
                    Exit<TWorkflowData> exit = pair.Item2;

                    workflow.IsComplete = true;
                    OnWorkflowCompleted(workflow, exit);
                    _activeWorkflows.Remove(workflow);
                }

                foreach (var pair in failedWorkflows)
                {
                    Workflow<TWorkflowData> workflow = pair.Item1;
                    Exception error = pair.Item2;

                    workflow.IsFaulted = true;
                    OnWorkflowFailed(workflow, error);
                    _activeWorkflows.Remove(workflow);
                }

                //if (fetchTask != null)
                //{
                //    IEnumerable<object> data = null;

                //    if (!fetchTask.Wait(timeout))
                //    {
                //        Trace.TraceWarning("Took too long to fetch workflow data");
                //    }
                //    else if (fetchTask.IsFaulted)
                //    {
                //        Trace.TraceWarning($"Fetch failed: {fetchTask.Exception}");
                //    }
                //    else
                //    {
                //        data = fetchTask.Result;
                //    }

                //    if (data != null)
                //    {
                //        foreach (object datum in data)
                //        {
                //            StartWorkflow(datum);
                //        }
                //    }
                //}

                _totalStepTime += _stepTimer.Elapsed;
            }
        }

        private bool EvaluateCondition(Predicate<TWorkflowData> condition, Workflow<TWorkflowData> workflow)
        {
            if (condition == null)
            {
                return true;
            }

            return condition(workflow.Data);
        }

        public event EventHandler<WorkflowEventArgs<TWorkflowData>>             WorkflowCompleted;
        public event EventHandler<WorkflowTransitionedEventArgs<TWorkflowData>> WorkflowTransitioned;
        public event EventHandler<WorkflowFailedEventArgs<TWorkflowData>>       WorkflowFailed;
        public event EventHandler<WorkflowTransitionedEventArgs<TWorkflowData>> SubordinateTransitioned;

        private void OnWorkflowCompleted(Workflow<TWorkflowData> workflow, Exit<TWorkflowData> exit)
        {
            if (exit.Action != null)
            {
                exit.Action(workflow.Data);
            }

            if (WorkflowCompleted != null)
            {
                WorkflowCompleted(this, new WorkflowEventArgs<TWorkflowData>(workflow));
            }
        }

        private void OnWorkflowTransitioned(Workflow<TWorkflowData> workflow, Transition<TWorkflowData> transition)
        {
            Debug.Assert(workflow.CurrentState.Equals(transition.To));

            if (transition.Action != null)
            {
                transition.Action(workflow.Data);
            }

            if (WorkflowTransitioned != null)
            {
                var args = new WorkflowTransitionedEventArgs<TWorkflowData>(transition, workflow);
                WorkflowTransitioned(this, args);
            }

            StartSubordinate(workflow);
        }

        private void OnWorkflowFailed(Workflow<TWorkflowData> workflow, Exception exception)
        {
            if (exception is TargetInvocationException)
            {
                exception = exception.InnerException;
            }

            if (WorkflowFailed != null)
            {
                WorkflowFailed(this, new WorkflowFailedEventArgs<TWorkflowData>(exception, workflow));
            }
        }

        private void OnSubordinateTransitioned(Workflow<TWorkflowData> workflow, ITransition transition)
        {
            if (SubordinateTransitioned != null)
            {
                var args = new WorkflowTransitionedEventArgs<TWorkflowData>(transition, workflow);
                SubordinateTransitioned(this, args);
            }
        }

        public string Name { get; private set; }
        public IEnumerable<IState> States => _allStates;
        public IEnumerable<ITransition> Transitions
        {
            get
            {
                foreach (var state in States)
                {
                    foreach (var transition in state.Transitions)
                    {
                        yield return transition;
                    }
                }
            }
        }

        public void AddState(State state)
        {
            //var state = new State(stateName);

            //_states.Add(stateName);
            _allStates.Add(state);
            _statesByName.Add(state.Name, state);
        }
        
        public void AddTransition(Transition<TWorkflowData> transition)
        {
            List<Transition<TWorkflowData>> transitions;
            if (!_transitionsByFromState.TryGetValue(transition.From.Name, out transitions))
            {
                transitions = new List<Transition<TWorkflowData>>();
                _transitionsByFromState.Add(transition.From.Name, transitions);
            }

            transitions.Add(transition);
        }

        private List<Transition<TWorkflowData>> GetTransitions(string from)
        {
            List<Transition<TWorkflowData>> transitions;
            if (!_transitionsByFromState.TryGetValue(from, out transitions))
            {
                var empty = new List<Transition<TWorkflowData>>();
                _transitionsByFromState.Add(from, empty);
                return empty;
            }

            return transitions;
        }

        public void AddExit(Exit<TWorkflowData> exit)
        {
            List<Exit<TWorkflowData>> exits;
            if (!_exitConditionsByState.TryGetValue(exit.StateFrom, out exits))
            {
                exits = new List<Exit<TWorkflowData>>();
                _exitConditionsByState.Add(exit.StateFrom, exits);
            }

            exits.Add(exit);
        }

        private List<Exit<TWorkflowData>> GetExits(string from)
        {
            List<Exit<TWorkflowData>> conditions;
            if (!_exitConditionsByState.TryGetValue(from, out conditions))
            {
                var empty = new List<Exit<TWorkflowData>>();
                _exitConditionsByState.Add(from, empty);
                return empty;
            }

            return conditions;
        }
        
        public void AddTrigger(string state, Trigger<TWorkflowData> trigger)
        {
            var triggers = GetTriggers(state);

            //if (!triggers.Any())
            //{
            //    _triggersByState.Add(state, triggers);
            //}

            triggers.Add(trigger);
        }

        private List<Trigger<TWorkflowData>> GetTriggers(string stateName)
        {
            List<Trigger<TWorkflowData>> triggers;
            if (!_triggersByState.TryGetValue(stateName, out triggers))
            {
                var empty = new List<Trigger<TWorkflowData>>();
                _triggersByState.Add(stateName, empty);
                return empty;
            }

            return triggers;
        }

        public void SetErrorPolicyOverride(IErrorPolicy<TWorkflowData> errorPolicy, string stateName)
        {
            _errorPolicyOverridesByState[stateName] = errorPolicy;
        }

        public void AttachSubordinate(
            string stateName,
            IWorkflowEngine<TWorkflowData> subordinate)
            //where TSubordinateWorkflow : Workflow
        {
            // TODO: determine if event detachment is necessary
            IWorkflowEngine<TWorkflowData> currentSubordinate;
            if (_subordinateWorkflowsByState.TryGetValue(stateName, out currentSubordinate))
            {
                currentSubordinate.WorkflowCompleted -= Subordinate_WorkflowCompleted;
                currentSubordinate.WorkflowFailed -= Subordinate_WorkflowFailed;
                currentSubordinate.WorkflowTransitioned -= Subordinate_WorkflowTransitioned;
            }

            subordinate.WorkflowCompleted += Subordinate_WorkflowCompleted;
            subordinate.WorkflowFailed += Subordinate_WorkflowFailed;
            subordinate.WorkflowTransitioned += Subordinate_WorkflowTransitioned;

            _subordinateWorkflowsByState[stateName] = subordinate;
        }

        private IWorkflowEngine<TWorkflowData> GetSubordinate(string stateName)
        {
            IWorkflowEngine<TWorkflowData> subordinate;
            if (!_subordinateWorkflowsByState.TryGetValue(stateName, out subordinate))
            {
                return null;
            }

            return subordinate;
        }

        private void StartSubordinate(Workflow<TWorkflowData> parent)
        {
            var subordinateEngine = GetSubordinate(parent.CurrentState.Name);
            if (subordinateEngine == null)
            {
                return;
            }

            var subordinate = subordinateEngine.StartWorkflow(parent.Data);

            _parentWorkflowsByChildID.Add(subordinate.Id, parent);
            _workflowsAwaitingSubordinate.Add(parent);
        }

        private void Subordinate_WorkflowTransitioned(object sender, WorkflowTransitionedEventArgs<TWorkflowData> e)
        {
            Workflow<TWorkflowData> subordinate = e.Workflow;
            Workflow<TWorkflowData> parent = _parentWorkflowsByChildID[subordinate.Id];
            
            OnSubordinateTransitioned(parent, e.Transition);
        }

        private void Subordinate_WorkflowFailed(object sender, WorkflowFailedEventArgs<TWorkflowData> e)
        {
            Workflow<TWorkflowData> failedSubordinate = e.Workflow;
            Workflow<TWorkflowData> parent = _parentWorkflowsByChildID[failedSubordinate.Id];

            _parentWorkflowsByChildID.Remove(failedSubordinate.Id);

            IErrorPolicy<TWorkflowData> errorPolicy = GetErrorPolicy(parent.CurrentState.Name);
            if (!errorPolicy.ShouldContinue(parent.Data, e.Exception))
            {
                OnWorkflowFailed(parent, e.Exception);
                _activeWorkflows.Remove(parent);
                _workflowsAwaitingSubordinate.Remove(parent);
                return;
            }

            // what is the Right Thing To Do here?
            // restart the subordinate seems most likely
            // TODO: consider alternatives
            StartSubordinate(parent);
        }

        private void Subordinate_WorkflowCompleted(object sender, WorkflowEventArgs<TWorkflowData> e)
        {
            Workflow<TWorkflowData> subordinate = e.Workflow;
            Workflow<TWorkflowData> parent = _parentWorkflowsByChildID[subordinate.Id];

            _parentWorkflowsByChildID.Remove(subordinate.Id);
            _workflowsAwaitingSubordinate.Remove(parent);
        }

        private IErrorPolicy<TWorkflowData> GetErrorPolicy(string stateName)
        {
            IErrorPolicy<TWorkflowData> errorPolicy;
            if (_errorPolicyOverridesByState.TryGetValue(stateName, out errorPolicy))
            {
                return errorPolicy;
            }

            return ErrorPolicy;
        }
        
        public bool ContainsState(string stateName)
        {
            return _stateNames.Contains(stateName);
        }

        public bool ContainsStates(params string[] stateNames)
        {
            if (stateNames == null)
            {
                return false;
            }

            return stateNames.All(ContainsState);
        }

        public IState InitialState
        {
            get { return _initialState; }
            set
            {
                _initialState = value;
                if (!_stateNames.Contains(value.Name))
                {
                    _stateNames.Add(value.Name);
                }
            }
        }

        //public IEnumerable<string> States => _states.ToList();

        public IErrorPolicy<TWorkflowData> ErrorPolicy
        {
            get;
            set;
        }

        //public IWorkflowFactory<TWorkflowData> WorkflowFactory
        //{
        //    get;
        //    set;
        //}

        //public IWorkflowDataRetriever<TWorkflowData> WorkflowDataRetriever
        //{
        //    get;
        //    set;
        //}

        public IWorkflowStorage<TWorkflowData> WorkflowStorage
        {
            get;
            set;
        }

        public IReadOnlyList<Workflow<TWorkflowData>> ActiveWorkflows => _activeWorkflows;

        public TimeSpan AverageStepTime
        {
            get
            {
                long stepTicks = _totalStepTime.Ticks;
                long averageTicks = stepTicks / _stepCount;
                return TimeSpan.FromTicks(averageTicks);
            }
        }

        public int StepCount => _stepCount;

        private IEnumerable<IWorkflowEngine<TWorkflowData>>
            Subordinates => _subordinateWorkflowsByState.Values;

        public WorkflowEngine(string name)
        {
            Name = name;
        }

        private readonly object _stepLock = new object();
        private int _stepCount = 0;
        private TimeSpan _totalStepTime = TimeSpan.Zero;
        private readonly Stopwatch _stepTimer = new Stopwatch();
        private readonly Stopwatch _runTimer = new Stopwatch();
        private IState _initialState;
        private readonly HashSet<string> _stateNames = new HashSet<string>();

        private readonly Dictionary<string, List<Transition<TWorkflowData>>>
            _transitionsByFromState = new Dictionary<string, List<Transition<TWorkflowData>>>();

        private readonly Dictionary<string, List<Exit<TWorkflowData>>>
            _exitConditionsByState = new Dictionary<string, List<Exit<TWorkflowData>>>();

        private readonly Dictionary<string, List<Trigger<TWorkflowData>>>
            _triggersByState = new Dictionary<string, List<Trigger<TWorkflowData>>>();

        private readonly List<Workflow<TWorkflowData>>
            _activeWorkflows = new List<Workflow<TWorkflowData>>();

        private readonly Dictionary<string, IErrorPolicy<TWorkflowData>>
            _errorPolicyOverridesByState = new Dictionary<string, IErrorPolicy<TWorkflowData>>();

        private readonly Dictionary<string, IWorkflowEngine<TWorkflowData>>
            _subordinateWorkflowsByState = new Dictionary<string, IWorkflowEngine<TWorkflowData>>();

        private readonly Dictionary<Guid, Workflow<TWorkflowData>>
            _parentWorkflowsByChildID = new Dictionary<Guid, Workflow<TWorkflowData>>();

        private readonly HashSet<Workflow<TWorkflowData>>
            _workflowsAwaitingSubordinate = new HashSet<Workflow<TWorkflowData>>();

        private readonly HashSet<State> _allStates
            = new HashSet<State>();
        private readonly Dictionary<string, State> _statesByName
            = new Dictionary<string, State>();
    }

    internal class State : IState
    {
        public string Name { get; private set; }
        public string Description { get; private set; }

        public IReadOnlyList<ITransition> Transitions 
            => TransitionsInternal;

        public List<ITransition> TransitionsInternal { get; set; }
            = new List<ITransition>();

        public State(string name, string description)
        {
            Name = name;
            Description = description ?? "";
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            IState other = obj as IState;
            if (other == null)
            {
                return false;
            }

            return Name.Equals(other.Name);
        }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }

        public override string ToString()
        {
            if (String.IsNullOrWhiteSpace(Description))
            {
                return Name;
            }

            return String.Format("{0}: {1}", Name, Description);
        }
    }

    internal class Trigger<TWorkflowData> where TWorkflowData : class
    {
        public Predicate<TWorkflowData> Condition { get; private set; }
        public Action<TWorkflowData> Action { get; private set; }
        public TimeSpan? RateLimit { get; private set; }
        public bool IsRateLimited { get { return RateLimit.HasValue; } }
        public TimeSpan? TimeLastTriggered { get; internal set; }

        public Trigger(Predicate<TWorkflowData> condition, Action<TWorkflowData> action, TimeSpan? rateLimit = null)
        {
            Condition = condition;
            Action = action;
            RateLimit = rateLimit;
        }
    }

    internal class Transition<TWorkflowData> : ITransition where TWorkflowData : class
    {
        public IState From { get; private set; }
        public IState To { get; private set; }
        public Predicate<TWorkflowData> Condition => _trigger.Condition; 
        public Action<TWorkflowData> Action => _trigger.Action;

        public Transition(State from, State to, Predicate<TWorkflowData> condition, Action<TWorkflowData> action)
        {
            _trigger = new Trigger<TWorkflowData>(condition, action);
            From = from;
            To = to;
        }

        public override string ToString()
        {
            return $"{From.Name} -> {To.Name}";
        }

        private readonly Trigger<TWorkflowData> _trigger;
    }

    internal class Exit<TWorkflowData> where TWorkflowData : class
    {
        public string StateFrom { get; private set; }
        public Predicate<TWorkflowData> Condition => _trigger.Condition;
        public Action<TWorkflowData> Action => _trigger.Action;

        public Exit(string from, Predicate<TWorkflowData> condition, Action<TWorkflowData> action)
        {
            _trigger = new Trigger<TWorkflowData>(condition, action);
            StateFrom = from;
        }

        private readonly Trigger<TWorkflowData> _trigger;
    }
}
