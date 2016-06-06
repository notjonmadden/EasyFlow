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
    class WorkflowEngine<TWorkflow> : IWorkflowEngine<TWorkflow> where TWorkflow : Workflow
    {
        public TWorkflow StartWorkflow(object data)
        {
            TWorkflow workflow = WorkflowFactory.Create(data);

            workflow.CurrentState = InitialState;
            workflow.IsComplete = false;
            workflow.Id = Guid.NewGuid();

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

                Task<IEnumerable<object>> fetchTask;
                TimeSpan timeout;
                if (WorkflowDataRetriever != null)
                {
                    fetchTask = Task.Run(() => WorkflowDataRetriever.FetchWorkflowData());
                    timeout = WorkflowDataRetriever.FetchTimeout;
                }
                else
                {
                    fetchTask = Task.FromResult(Enumerable.Empty<object>());
                    timeout = TimeSpan.Zero;
                }

                foreach (var subordinateEngine in Subordinates)
                {
                    if (!subordinateEngine.ActiveWorkflows.Any())
                    {
                        continue;
                    }

                    subordinateEngine.Step();
                }

                var exitedWorkflows = new List<Tuple<TWorkflow, Exit<TWorkflow>>>();
                var failedWorkflows = new List<Tuple<TWorkflow, Exception>>();
                foreach (var workflow in _activeWorkflows)
                {
                    if (_workflowsAwaitingSubordinate.Contains(workflow))
                    {
                        continue;
                    }

                    workflow.OnBeginStep();

                    IState state = workflow.CurrentState;
                    IErrorPolicy<TWorkflow> errorPolicy = GetErrorPolicy(state.Name);

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
                        if (!errorPolicy.ShouldContinue(workflow, ex))
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
                        if (!errorPolicy.ShouldContinue(workflow, ex))
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
                                trigger.Action(workflow);
                                trigger.TimeLastTriggered = runTime;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!errorPolicy.ShouldContinue(workflow, ex))
                        {
                            failedWorkflows.Add(Tuple.Create(workflow, ex));
                            continue;
                        }
                    }
                }

                foreach (var pair in exitedWorkflows)
                {
                    TWorkflow workflow = pair.Item1;
                    Exit<TWorkflow> exit = pair.Item2;

                    workflow.IsComplete = true;
                    OnWorkflowCompleted(workflow, exit);
                    _activeWorkflows.Remove(workflow);
                }

                foreach (var pair in failedWorkflows)
                {
                    TWorkflow workflow = pair.Item1;
                    Exception error = pair.Item2;

                    workflow.IsFaulted = true;
                    OnWorkflowFailed(workflow, error);
                    _activeWorkflows.Remove(workflow);
                }

                if (fetchTask != null)
                {
                    IEnumerable<object> data = null;

                    if (!fetchTask.Wait(timeout))
                    {
                        Trace.TraceWarning("Took too long to fetch workflow data");
                    }
                    else if (fetchTask.IsFaulted)
                    {
                        Trace.TraceWarning($"Fetch failed: {fetchTask.Exception}");
                    }
                    else
                    {
                        data = fetchTask.Result;
                    }

                    if (data != null)
                    {
                        foreach (object datum in data)
                        {
                            StartWorkflow(datum);
                        }
                    }
                }

                _totalStepTime += _stepTimer.Elapsed;
            }
        }

        private bool EvaluateCondition(Predicate<TWorkflow> condition, TWorkflow workflow)
        {
            if (condition == null)
            {
                return true;
            }

            return condition(workflow);
        }

        public event EventHandler<WorkflowEventArgs>             WorkflowCompleted;
        public event EventHandler<WorkflowTransitionedEventArgs> WorkflowTransitioned;
        public event EventHandler<WorkflowFailedEventArgs>       WorkflowFailed;
        public event EventHandler<WorkflowTransitionedEventArgs> SubordinateTransitioned;

        private void OnWorkflowCompleted(TWorkflow workflow, Exit<TWorkflow> exit)
        {
            if (exit.Action != null)
            {
                exit.Action(workflow);
            }

            if (WorkflowCompleted != null)
            {
                WorkflowCompleted(this, new WorkflowEventArgs(workflow));
            }
        }

        private void OnWorkflowTransitioned(TWorkflow workflow, Transition<TWorkflow> transition)
        {
            Debug.Assert(workflow.CurrentState.Equals(transition.To));

            if (transition.Action != null)
            {
                transition.Action(workflow);
            }

            if (WorkflowTransitioned != null)
            {
                var args = new WorkflowTransitionedEventArgs(transition, workflow);
                WorkflowTransitioned(this, args);
            }

            StartSubordinate(workflow);
        }

        private void OnWorkflowFailed(TWorkflow workflow, Exception exception)
        {
            if (exception is TargetInvocationException)
            {
                exception = exception.InnerException;
            }

            if (WorkflowFailed != null)
            {
                WorkflowFailed(this, new WorkflowFailedEventArgs(exception, workflow));
            }
        }

        private void OnSubordinateTransitioned(TWorkflow workflow, ITransition transition)
        {
            if (SubordinateTransitioned != null)
            {
                var args = new WorkflowTransitionedEventArgs(transition, workflow);
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
        
        public void AddTransition(Transition<TWorkflow> transition)
        {
            List<Transition<TWorkflow>> transitions;
            if (!_transitionsByFromState.TryGetValue(transition.From.Name, out transitions))
            {
                transitions = new List<Transition<TWorkflow>>();
                _transitionsByFromState.Add(transition.From.Name, transitions);
            }

            transitions.Add(transition);
        }

        private List<Transition<TWorkflow>> GetTransitions(string from)
        {
            List<Transition<TWorkflow>> transitions;
            if (!_transitionsByFromState.TryGetValue(from, out transitions))
            {
                var empty = new List<Transition<TWorkflow>>();
                _transitionsByFromState.Add(from, empty);
                return empty;
            }

            return transitions;
        }

        public void AddExit(Exit<TWorkflow> exit)
        {
            List<Exit<TWorkflow>> exits;
            if (!_exitConditionsByState.TryGetValue(exit.StateFrom, out exits))
            {
                exits = new List<Exit<TWorkflow>>();
                _exitConditionsByState.Add(exit.StateFrom, exits);
            }

            exits.Add(exit);
        }

        private List<Exit<TWorkflow>> GetExits(string from)
        {
            List<Exit<TWorkflow>> conditions;
            if (!_exitConditionsByState.TryGetValue(from, out conditions))
            {
                var empty = new List<Exit<TWorkflow>>();
                _exitConditionsByState.Add(from, empty);
                return empty;
            }

            return conditions;
        }
        
        public void AddTrigger(string state, Trigger<TWorkflow> trigger)
        {
            var triggers = GetTriggers(state);

            if (!triggers.Any())
            {
                _triggersByState.Add(state, triggers);
            }

            triggers.Add(trigger);
        }

        private List<Trigger<TWorkflow>> GetTriggers(string stateName)
        {
            List<Trigger<TWorkflow>> triggers;
            if (!_triggersByState.TryGetValue(stateName, out triggers))
            {
                var empty = new List<Trigger<TWorkflow>>();
                _triggersByState.Add(stateName, empty);
                return empty;
            }

            return triggers;
        }

        public void SetErrorPolicyOverride(IErrorPolicy<TWorkflow> errorPolicy, string stateName)
        {
            _errorPolicyOverridesByState[stateName] = errorPolicy;
        }

        public void AttachSubordinate<TSubordinateWorkflow>(
            string stateName,
            IWorkflowEngine<TSubordinateWorkflow> subordinate)
            where TSubordinateWorkflow : Workflow
        {
            // TODO: determine if event detachment is necessary
            IWorkflowEngine<Workflow> currentSubordinate;
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

        private IWorkflowEngine<Workflow> GetSubordinate(string stateName)
        {
            IWorkflowEngine<Workflow> subordinate;
            if (!_subordinateWorkflowsByState.TryGetValue(stateName, out subordinate))
            {
                return null;
            }

            return subordinate;
        }

        private void StartSubordinate(TWorkflow parent)
        {
            var subordinateEngine = GetSubordinate(parent.CurrentState.Name);
            if (subordinateEngine == null)
            {
                return;
            }

            var subordinate = subordinateEngine.StartWorkflow(parent);

            _parentWorkflowsByChildID.Add(subordinate.Id, parent);
            _workflowsAwaitingSubordinate.Add(parent);
        }

        private void Subordinate_WorkflowTransitioned(object sender, WorkflowTransitionedEventArgs e)
        {
            Workflow subordinate = e.Workflow;
            TWorkflow parent = _parentWorkflowsByChildID[subordinate.Id];
            
            OnSubordinateTransitioned(parent, e.Transition);
        }

        private void Subordinate_WorkflowFailed(object sender, WorkflowFailedEventArgs e)
        {
            Workflow failedSubordinate = e.Workflow;
            TWorkflow parent = _parentWorkflowsByChildID[failedSubordinate.Id];

            _parentWorkflowsByChildID.Remove(failedSubordinate.Id);

            IErrorPolicy<TWorkflow> errorPolicy = GetErrorPolicy(parent.CurrentState.Name);
            if (!errorPolicy.ShouldContinue(parent, e.Exception))
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

        private void Subordinate_WorkflowCompleted(object sender, WorkflowEventArgs e)
        {
            Workflow subordinate = e.Workflow;
            TWorkflow parent = _parentWorkflowsByChildID[subordinate.Id];

            _parentWorkflowsByChildID.Remove(subordinate.Id);
            _workflowsAwaitingSubordinate.Remove(parent);
        }

        private IErrorPolicy<TWorkflow> GetErrorPolicy(string stateName)
        {
            IErrorPolicy<TWorkflow> errorPolicy;
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

        public IErrorPolicy<TWorkflow> ErrorPolicy
        {
            get;
            set;
        }

        public IWorkflowFactory<TWorkflow> WorkflowFactory
        {
            get;
            set;
        }

        public IWorkflowDataRetriever<TWorkflow> WorkflowDataRetriever
        {
            get;
            set;
        }

        public IWorkflowStorage<TWorkflow> WorkflowStorage
        {
            get;
            set;
        }

        public IReadOnlyList<TWorkflow> ActiveWorkflows => _activeWorkflows;

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

        private IEnumerable<IWorkflowEngine<Workflow>>
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
        private readonly Dictionary<string, List<Transition<TWorkflow>>>
            _transitionsByFromState = new Dictionary<string, List<Transition<TWorkflow>>>();
        private readonly Dictionary<string, List<Exit<TWorkflow>>>
            _exitConditionsByState = new Dictionary<string, List<Exit<TWorkflow>>>();
        private readonly Dictionary<string, List<Trigger<TWorkflow>>>
            _triggersByState = new Dictionary<string, List<Trigger<TWorkflow>>>();
        private readonly List<TWorkflow> _activeWorkflows = new List<TWorkflow>();
        private readonly Dictionary<string, IErrorPolicy<TWorkflow>>
            _errorPolicyOverridesByState = new Dictionary<string, IErrorPolicy<TWorkflow>>();
        private readonly Dictionary<string, IWorkflowEngine<Workflow>>
            _subordinateWorkflowsByState = new Dictionary<string, IWorkflowEngine<Workflow>>();
        private readonly Dictionary<Guid, TWorkflow>
            _parentWorkflowsByChildID = new Dictionary<Guid, TWorkflow>();
        private readonly HashSet<TWorkflow> _workflowsAwaitingSubordinate = new HashSet<TWorkflow>();

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

    internal class Trigger<TWorkflow> where TWorkflow : Workflow
    {
        public Predicate<TWorkflow> Condition { get; private set; }
        public Action<TWorkflow> Action { get; private set; }
        public TimeSpan? RateLimit { get; private set; }
        public bool IsRateLimited { get { return RateLimit.HasValue; } }
        public TimeSpan? TimeLastTriggered { get; internal set; }

        public Trigger(Predicate<TWorkflow> condition, Action<TWorkflow> action, TimeSpan? rateLimit = null)
        {
            Condition = condition;
            Action = action;
            RateLimit = rateLimit;
        }
    }

    internal class Transition<TWorkflow> : ITransition where TWorkflow : Workflow
    {
        public IState From { get; private set; }
        public IState To { get; private set; }
        public Predicate<TWorkflow> Condition => _trigger.Condition; 
        public Action<TWorkflow> Action => _trigger.Action;

        public Transition(State from, State to, Predicate<TWorkflow> condition, Action<TWorkflow> action)
        {
            _trigger = new Trigger<TWorkflow>(condition, action);
            From = from;
            To = to;
        }

        private readonly Trigger<TWorkflow> _trigger;
    }

    internal class Exit<TWorkflow> where TWorkflow : Workflow
    {
        public string StateFrom { get; private set; }
        public Predicate<TWorkflow> Condition => _trigger.Condition;
        public Action<TWorkflow> Action => _trigger.Action;

        public Exit(string from, Predicate<TWorkflow> condition, Action<TWorkflow> action)
        {
            _trigger = new Trigger<TWorkflow>(condition, action);
            StateFrom = from;
        }

        private readonly Trigger<TWorkflow> _trigger;
    }
}
