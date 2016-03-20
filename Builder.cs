using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EasyFlow
{
    //  TODO: transition priority
    //  TODO: check for dead-end states (no outgoing transitions and no exits)
    // ?TODO: async workflow update
    // ?TODO: generic workflow factory, data retriever
    public class WorkflowEngineBuilder<TWorkflow> where TWorkflow : Workflow
    {
        public WorkflowEngineBuilder<TWorkflow> DefineEntryState(string stateName)
        {
            DefineState(stateName);
            _entryState = stateName;

            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> DefineState(string stateName)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            if (IsWildcard(stateName))
            {
                throw new ArgumentException(IllegalCharInStateNameMessage);
            }

            _states.Add(stateName);

            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> DefineStates(params string[] stateNames)
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

        public WorkflowEngineBuilder<TWorkflow> DefineTransition(
            string fromState, string toState,
            Predicate<TWorkflow> condition = null,
            Action<TWorkflow> action = null)
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
            
            if (!_states.Contains(toState))
            {
                throw new ArgumentException("Cannot transition to non-existent state. Make sure toState is defined.");
            }

            if (IsWildcard(toState))
            {
                throw new ArgumentNullException("toState cannot contain '*'. Wildcards are only supported for fromState.");
            }
            
            if (!IsWildcard(fromState))
            {
                if (!_states.Contains(fromState))
                {
                    throw new ArgumentException("Cannot transition from non-existent state. Make sure fromState is defined.");
                }

                var transition = new Transition<TWorkflow>(fromState, toState, condition, action);
                _transitions.Add(transition);
            }
            else
            {
                foreach (string state in MatchingStates(fromState))
                {
                    if (state.Equals(toState))
                    {
                        continue;
                    }

                    DefineTransition(state, toState, condition, action);
                }
            }
            
            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> DefineExit(
            string stateName,
            Predicate<TWorkflow> exitCondition = null,
            Action<TWorkflow> action = null)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }
            
            if (!IsWildcard(stateName))
            {
                if (!_states.Contains(stateName))
                {
                    throw new ArgumentException("Cannot exit from undefined state. Make sure the state is defined.");
                }

                var exit = new Exit<TWorkflow>(stateName, exitCondition, action);
                _exits.Add(exit);
            }
            else
            {
                foreach (string state in MatchingStates(stateName))
                {
                    DefineExit(state, exitCondition, action);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> DefineTrigger(
            string stateName,
            Action<TWorkflow> action,
            Predicate<TWorkflow> condition = null,
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
                if (!_states.Contains(stateName))
                {
                    throw new ArgumentException("Cannot create trigger on undefined state. Make sure the state is defined.");
                }

                var trigger = new Trigger<TWorkflow>(condition, action, rateLimit);
                _triggers.Add(Tuple.Create(stateName, trigger));
            }
            else
            {
                foreach (string state in MatchingStates(stateName))
                {
                    DefineTrigger(state, action, condition, rateLimit);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> SetErrorPolicy(ErrorPolicy policy)
        {
            if (!Enum.IsDefined(typeof(ErrorPolicy), policy))
            {
                throw new ArgumentException($"{nameof(policy)} is not a defined {typeof(ErrorPolicy).Name}");
            }

            switch (policy)
            {
                case ErrorPolicy.StopWorkflow:
                    return SetErrorPolicy(new ErrorPolicyStopWorkflow<TWorkflow>());

                case ErrorPolicy.Ignore:
                    return SetErrorPolicy(new ErrorPolicyIgnore<TWorkflow>());
                
                case ErrorPolicy.Throw:
                default:
                    return SetErrorPolicy(new ErrorPolicyThrow<TWorkflow>());
            }
        }

        public WorkflowEngineBuilder<TWorkflow> SetErrorPolicy(string stateName, ErrorPolicy policy)
        {
            if (!Enum.IsDefined(typeof(ErrorPolicy), policy))
            {
                throw new ArgumentException($"{nameof(policy)} is not a defined {typeof(ErrorPolicy).Name}");
            }

            switch (policy)
            {
                case ErrorPolicy.StopWorkflow:
                    return SetErrorPolicy(new ErrorPolicyStopWorkflow<TWorkflow>(), stateName);

                case ErrorPolicy.Ignore:
                    return SetErrorPolicy(new ErrorPolicyIgnore<TWorkflow>(), stateName);

                case ErrorPolicy.Throw:
                default:
                    return SetErrorPolicy(new ErrorPolicyThrow<TWorkflow>(), stateName);
            }
        }

        public WorkflowEngineBuilder<TWorkflow> SetErrorPolicy(IErrorPolicy<TWorkflow> policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            _errorPolicy = policy;
            //_engine.ErrorPolicy = policy;
            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> SetErrorPolicy(IErrorPolicy<TWorkflow> policy, string stateName)
        {
            if (String.IsNullOrWhiteSpace(stateName))
            {
                throw new ArgumentException(NullStateNameMessage);
            }

            // actually set the policy.
            if (!IsWildcard(stateName))
            {
                if (!_states.Contains(stateName))
                {
                    throw new ArgumentException("Cannot set error policy on undefined state. Make sure it is defined");
                }

                _errorPolicyOverridesByState[stateName] = policy;
            }
            else
            {
                foreach (string state in MatchingStates(stateName))
                {
                    SetErrorPolicy(policy, state);
                }
            }

            return this;
        }

        public WorkflowEngineBuilder<TWorkflow> AttachSubordinateEngine<TSubordinateWorkflow>(
            string stateName,
            IWorkflowEngine<TSubordinateWorkflow> subordinateEngine) 
            where TSubordinateWorkflow : Workflow
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

            return this;
        }

        public IWorkflowEngine<TWorkflow> BuildEngine(
            IWorkflowFactory<TWorkflow>   workflowFactory,
            IWorkflowDataRetriever<TWorkflow> workflowRetriever = null,
            IWorkflowStorage<TWorkflow>   workflowStorage   = null)
        {
            if (workflowFactory == null)
            {
                throw new ArgumentNullException(nameof(workflowFactory));
            }

            if (_entryState == null)
            {
                throw new InvalidOperationException("cannot build engine without entry state.");
            }

            if (_errorPolicy == null)
            {
                SetErrorPolicy(ErrorPolicy.Default);
            }

            var engine = new WorkflowEngine<TWorkflow>();

            engine.InitialState = _entryState;
            engine.ErrorPolicy = _errorPolicy;
            
            foreach (string state in _states)
            {
                engine.AddState(state);
            }

            foreach (var transition in _transitions)
            {
                engine.AddTransition(transition);
            }

            foreach (var pair in _triggers)
            {
                string state = pair.Item1;
                Trigger<TWorkflow> trigger = pair.Item2;

                engine.AddTrigger(state, trigger);
            }

            foreach (var exit in _exits)
            {
                engine.AddExit(exit);
            }

            foreach (var policyOverride in _errorPolicyOverridesByState)
            {
                string state = policyOverride.Key;
                IErrorPolicy<TWorkflow> policy = policyOverride.Value;

                engine.SetErrorPolicyOverride(policy, state);
            }

            engine.WorkflowDataRetriever = workflowRetriever;
            engine.WorkflowFactory       = workflowFactory;
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

        private IEnumerable<string> MatchingStates(string wildcard)
        {
            string pattern = wildcard.Replace("*", "(.*)");
            var regex = new Regex(pattern);

            return _states.Where(state => regex.IsMatch(state));
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

        private string _entryState = null;
        private readonly HashSet<string> _states = new HashSet<string>();
        private readonly List<Transition<TWorkflow>> _transitions = new List<Transition<TWorkflow>>();
        private readonly List<Tuple<string, Trigger<TWorkflow>>> _triggers = new List<Tuple<string, Trigger<TWorkflow>>>();
        private readonly List<Exit<TWorkflow>> _exits = new List<Exit<TWorkflow>>();
        private IErrorPolicy<TWorkflow> _errorPolicy = null;
        private Dictionary<string, IErrorPolicy<TWorkflow>>
            _errorPolicyOverridesByState = new Dictionary<string, IErrorPolicy<TWorkflow>>();
        private Dictionary<string, IWorkflowEngine<Workflow>>
            _subordinateWorkflowsByState = new Dictionary<string, IWorkflowEngine<Workflow>>();

        private static readonly char[] IllegalCharacters = new[] { '*', ':' };

        private const string NullStateNameMessage = "state name cannot be null, empty, or whitespace.";
        private static readonly string IllegalCharInStateNameMessage
            = "state name may not contain any of the following characters: "
            + String.Join(", ", IllegalCharacters);
    }
}
