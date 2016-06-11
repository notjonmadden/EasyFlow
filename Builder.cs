using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EasyFlow
{
    //  TODO: TEST transition builder
    //  TODO: transition priority
    //  TODO: check for dead-end states (no outgoing transitions and no exits)
    // ?TODO: async workflow update
    // ?TODO: generic workflow factory, data retriever
    public class WorkflowEngineBuilder<TWorkflowData> where TWorkflowData : class
    {
        public WorkflowEngineBuilder<TWorkflowData> DefineEntryState(string stateName)
        {
            DefineState(stateName);
            _entryState = _statesByName[stateName];

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineState(string stateName, string description = null)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (IsWildcard(stateName))
            {
                throw new ArgumentException(IllegalCharInStateNameMessage);
            }

            var state = new State(stateName, description);

            _statesByName.Add(stateName, state);

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineStates(params string[] stateNames)
        {
            if (stateNames == null)
            {
                throw new ArgumentNullException(nameof(stateNames));
            }

            foreach (string stateName in stateNames)
            {
                DefineState(stateName);
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineTransition(
            string fromState, string toState,
            Predicate<TWorkflowData> condition = null,
            Action<TWorkflowData> action = null)
        {
            if (String.IsNullOrWhiteSpace(fromState)
                || String.IsNullOrWhiteSpace(toState))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (fromState.Equals(toState))
            {
                throw new ArgumentException("Cannot transition from a state to itself. Use a trigger instead.");
            }
            
            if (!IsStateDefined(toState))
            {
                throw new ArgumentException("Cannot transition to non-existent state. Make sure toState is defined.");
            }

            if (IsWildcard(toState))
            {
                throw new ArgumentNullException("toState cannot contain '*'. Wildcards are only supported for fromState.");
            }

            State from = GetState(fromState);
            State to = GetState(toState);

            if (!IsWildcard(fromState))
            {
                if (!IsStateDefined(fromState))
                {
                    throw new ArgumentException("Cannot transition from non-existent state. Make sure fromState is defined.");
                }

                var transition = new Transition<TWorkflowData>(from, to, condition, action);
                _transitions.Add(transition);
            }
            else
            {
                foreach (State state in MatchingStates(fromState))
                {
                    if (state.Equals(to))
                    {
                        continue;
                    }

                    DefineTransition(state.Name, toState, condition, action);
                }
            }
            
            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineTransition(Action<TransitionBuilder<TWorkflowData>> config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var builder = new TransitionBuilder<TWorkflowData>();

            config(builder);

            builder.BuildTransition(this);

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineExit(
            string stateName,
            Predicate<TWorkflowData> exitCondition = null,
            Action<TWorkflowData> action = null)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }
            
            if (!IsWildcard(stateName))
            {
                if (!IsStateDefined(stateName))
                {
                    throw new ArgumentException("Cannot exit from undefined state. Make sure the state is defined.");
                }

                var exit = new Exit<TWorkflowData>(stateName, exitCondition, action);
                _exits.Add(exit);
            }
            else
            {
                foreach (State state in MatchingStates(stateName))
                {
                    DefineExit(state.Name, exitCondition, action);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> DefineTrigger(
            string stateName,
            Action<TWorkflowData> action,
            Predicate<TWorkflowData> condition = null,
            TimeSpan? rateLimit = null)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (action == null)
            {
                throw new ArgumentException("cannot define trigger without an action");
            }

            if (!IsWildcard(stateName))
            {
                if (!IsStateDefined(stateName))
                {
                    throw new ArgumentException("Cannot create trigger on undefined state. Make sure the state is defined.");
                }

                var trigger = new Trigger<TWorkflowData>(condition, action, rateLimit);
                _triggers.Add(Tuple.Create(stateName, trigger));
            }
            else
            {
                foreach (State state in MatchingStates(stateName))
                {
                    DefineTrigger(state.Name, action, condition, rateLimit);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> SetErrorPolicy(ErrorPolicy policy)
        {
            if (!Enum.IsDefined(typeof(ErrorPolicy), policy))
            {
                throw new ArgumentException($"{nameof(policy)} is not a defined {typeof(ErrorPolicy).Name}");
            }

            switch (policy)
            {
                case ErrorPolicy.StopWorkflow:
                    return SetErrorPolicy(new ErrorPolicyStopWorkflow<TWorkflowData>());

                case ErrorPolicy.Ignore:
                    return SetErrorPolicy(new ErrorPolicyIgnore<TWorkflowData>());
                
                case ErrorPolicy.Throw:
                default:
                    return SetErrorPolicy(new ErrorPolicyThrow<TWorkflowData>());
            }
        }

        public WorkflowEngineBuilder<TWorkflowData> SetErrorPolicy(string stateName, ErrorPolicy policy)
        {
            if (!Enum.IsDefined(typeof(ErrorPolicy), policy))
            {
                throw new ArgumentException($"{nameof(policy)} is not a defined {typeof(ErrorPolicy).Name}");
            }

            switch (policy)
            {
                case ErrorPolicy.StopWorkflow:
                    return SetErrorPolicy(new ErrorPolicyStopWorkflow<TWorkflowData>(), stateName);

                case ErrorPolicy.Ignore:
                    return SetErrorPolicy(new ErrorPolicyIgnore<TWorkflowData>(), stateName);

                case ErrorPolicy.Throw:
                default:
                    return SetErrorPolicy(new ErrorPolicyThrow<TWorkflowData>(), stateName);
            }
        }

        public WorkflowEngineBuilder<TWorkflowData> SetErrorPolicy(IErrorPolicy<TWorkflowData> policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            _errorPolicy = policy;
            //_engine.ErrorPolicy = policy;
            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> SetErrorPolicy(IErrorPolicy<TWorkflowData> policy, string stateName)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            // actually set the policy.
            if (!IsWildcard(stateName))
            {
                if (!IsStateDefined(stateName))
                {
                    throw new ArgumentException("Cannot set error policy on undefined state. Make sure it is defined");
                }

                _errorPolicyOverridesByState[stateName] = policy;
            }
            else
            {
                foreach (State state in MatchingStates(stateName))
                {
                    SetErrorPolicy(policy, state.Name);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflowData> AttachSubordinateEngine(
            string stateName,
            IWorkflowEngine<TWorkflowData> subordinateEngine) 
            //where TSubordinateWorkflow : Workflow
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (IsWildcard(stateName))
            {
                throw new ArgumentException(
                    "wildcards are not supported for attaching subordinate engines. "
                    + IllegalCharInStateNameMessage
                );
            }

            if (_subordinateWorkflowsByState.ContainsKey(stateName))
            {
                throw new ArgumentException("cannot define multiple subordinate workflows for a single state");
            }

            _subordinateWorkflowsByState.Add(stateName, subordinateEngine);

            return this;
        }

        public IWorkflowEngine<TWorkflowData> BuildEngine(
            //IWorkflowFactory<TWorkflow>   workflowFactory,
            //IWorkflowDataRetriever<TWorkflow> workflowRetriever = null,
            IWorkflowStorage<TWorkflowData> workflowStorage   = null)
        {
            //if (workflowFactory == null)
            //{
            //    throw new ArgumentNullException(nameof(workflowFactory));
            //}

            if (_entryState == null)
            {
                throw new InvalidOperationException("cannot build engine without entry state.");
            }

            if (_errorPolicy == null)
            {
                SetErrorPolicy(ErrorPolicy.Default);
            }

            var engine = new WorkflowEngine<TWorkflowData>(_engineName);

            engine.InitialState = _entryState;
            engine.ErrorPolicy = _errorPolicy;
            
            foreach (State state in States)
            {
                engine.AddState(state);
            }

            foreach (var transition in _transitions)
            {
                var state = GetState(transition.From.Name);

                state.TransitionsInternal.Add(transition);
                engine.AddTransition(transition);
            }

            foreach (var pair in _triggers)
            {
                string state = pair.Item1;
                Trigger<TWorkflowData> trigger = pair.Item2;

                engine.AddTrigger(state, trigger);
            }

            foreach (var exit in _exits)
            {
                engine.AddExit(exit);
            }

            foreach (var policyOverride in _errorPolicyOverridesByState)
            {
                string state = policyOverride.Key;
                IErrorPolicy<TWorkflowData> policy = policyOverride.Value;

                engine.SetErrorPolicyOverride(policy, state);
            }

            foreach (var subordinateAttachment in _subordinateWorkflowsByState)
            {
                string state = subordinateAttachment.Key;
                var subordinateEngine = subordinateAttachment.Value;

                engine.AttachSubordinate(state, subordinateEngine);
            }

            //engine.WorkflowDataRetriever = workflowRetriever;
            //engine.WorkflowFactory       = workflowFactory;
            engine.WorkflowStorage       = workflowStorage;

            return engine;
        }

        private static bool IsWildcard(string str)
        {
            if (str == null)
            {
                throw new ArgumentNullException(nameof(str));
            }

            return str.Contains('*');
        }

        private IEnumerable<State> MatchingStates(string wildcard)
        {
            string pattern = wildcard.Replace("*", "(.*)");
            var regex = new Regex(pattern);

            return States.Where(state => regex.IsMatch(state.Name));
        }

        private void ThrowIfStateNameIsInvalid(string stateName)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (IsWildcard(stateName))
            {
                throw new ArgumentException(IllegalCharInStateNameMessage);
            }
        }

        public WorkflowEngineBuilder(string engineName)
        {
            if (engineName == null)
            {
                throw new ArgumentNullException("engineName");
            }

            _engineName = engineName;
        }

        private IEnumerable<State> States => _statesByName.Values;

        private State GetState(string stateName)
        {
            State state;
            if (_statesByName.TryGetValue(stateName, out state))
            {
                return state;
            }

            return null;
        }

        private bool IsStateDefined(string stateName)
        {
            return _statesByName.ContainsKey(stateName);
        }

        private readonly string _engineName;
        private State _entryState = null;
        private readonly Dictionary<string, State>
            _statesByName = new Dictionary<string, State>();
        private readonly List<Transition<TWorkflowData>> _transitions = new List<Transition<TWorkflowData>>();
        private readonly List<Tuple<string, Trigger<TWorkflowData>>> _triggers = new List<Tuple<string, Trigger<TWorkflowData>>>();
        private readonly List<Exit<TWorkflowData>> _exits = new List<Exit<TWorkflowData>>();
        private IErrorPolicy<TWorkflowData> _errorPolicy = null;
        private Dictionary<string, IErrorPolicy<TWorkflowData>>
            _errorPolicyOverridesByState = new Dictionary<string, IErrorPolicy<TWorkflowData>>();
        private Dictionary<string, IWorkflowEngine<TWorkflowData>>
            _subordinateWorkflowsByState = new Dictionary<string, IWorkflowEngine<TWorkflowData>>();

        private static readonly char[] IllegalCharacters = new[] { '*', ':' };

        private const string NullStateNameMessage = "state name cannot be null, empty, or whitespace.";
        private static readonly string IllegalCharInStateNameMessage
            = "state name may not contain any of the following characters: "
            + String.Join(", ", IllegalCharacters);
    }

    public class TransitionBuilder<TWorkflowData> where TWorkflowData : class
    {
        public TransitionBuilder<TWorkflowData> From(string state)
        {
            _from.Add(state);
            return this;
        }

        public TransitionBuilder<TWorkflowData> From(IEnumerable<string> states)
        {
            foreach (var state in states)
            {
                _from.Add(state);
            }

            return this;
        }

        public TransitionBuilder<TWorkflowData> From(params string[] states)
        {
            return From(states);
        }

        public TransitionBuilder<TWorkflowData> To(string state)
        {
            _to = state;
            return this;
        }

        public TransitionBuilder<TWorkflowData> When(Predicate<TWorkflowData> condition)
        {
            _condition = condition;
            return this;
        }

        public TransitionBuilder<TWorkflowData> WhenAny(params Predicate<TWorkflowData>[] conditions)
        {
            _condition = w => conditions.All(c => c?.Invoke(w) ?? true);
            return this;
        }

        public TransitionBuilder<TWorkflowData> WhenAll(params Predicate<TWorkflowData>[] conditions)
        {
            _condition = w => conditions.Any(c => c?.Invoke(w) ?? true);
            return this;
        }

        public TransitionBuilder<TWorkflowData> WithAction(Action<TWorkflowData> action)
        {
            if (action != null)
            {
                _actions.Add(action);
            }

            return this;
        }

        public TransitionBuilder<TWorkflowData> WithActions(IEnumerable<Action<TWorkflowData>> actions)
        {
            foreach (var a in actions)
            {
                if (a != null)
                {
                    _actions.Add(a);
                }
            }

            return this;
        }

        public TransitionBuilder<TWorkflowData> WithActions(params Action<TWorkflowData>[] actions)
        {
            return WithActions(actions);
        }

        internal void BuildTransition(WorkflowEngineBuilder<TWorkflowData> engineBuilder)
        {
            Action<TWorkflowData> action = _actions.FirstOrDefault();
            foreach (var a in _actions.Skip(1))
            {
                action += a;
            }

            foreach (var from in _from)
            {
                engineBuilder.DefineTransition(from, _to, _condition, action);
            }
        }

        private readonly HashSet<string> _from = new HashSet<string>();
        private string _to;
        private readonly List<Action<TWorkflowData>> _actions = new List<Action<TWorkflowData>>();
        private Predicate<TWorkflowData> _condition;
    }
}
