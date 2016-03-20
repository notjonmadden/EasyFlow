using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using System.Linq;
using System.Diagnostics;

namespace EasyFlow
{
    public abstract class Workflow
    {
        public string CurrentState
        {
            get;
            internal set;
        } = null;

        public bool IsComplete
        {
            get;
            internal set;
        } = false;

        public bool IsFaulted
        {
            get;
            internal set;
        } = false;

        public Guid Id
        {
            get;
            internal set;
        } = default(Guid);

        public virtual void OnBeginStep() { }
    }
    
    public interface IWorkflowEngine<out TWorkflow> where TWorkflow : Workflow
    {
        TWorkflow StartWorkflow(object initialData);
        void Step();

        event EventHandler<WorkflowEventArgs>             WorkflowCompleted;
        event EventHandler<WorkflowTransitionedEventArgs> WorkflowTransitioned;
        event EventHandler<WorkflowFailedEventArgs>       WorkflowFailed;

        int StepCount { get; }
        TimeSpan AverageStepTime { get; }
    }

    public interface IWorkflowEngine<TWorkflow, TData> where TWorkflow : Workflow
    {
        TWorkflow StartWorkflow(TData initialData);
        void Step();

        event EventHandler<WorkflowEventArgs> WorkflowCompleted;
        event EventHandler<WorkflowTransitionedEventArgs> WorkflowTransitioned;
        event EventHandler<WorkflowFailedEventArgs> WorkflowFailed;
    }

    public interface ITransition
    {
        string StateFrom { get; }
        string StateTo { get; }
    }

    public interface IWorkflowDataRetriever<TWorkflow> where TWorkflow : Workflow
    {
        TimeSpan FetchTimeout { get; }
        IEnumerable<object> FetchWorkflowData();
    }

    public interface IWorkflowDataRetriever<TWorkflow, TData> where TWorkflow : Workflow
    {
        TimeSpan FetchTimeout { get; }
        IEnumerable<TData> FetchWorkflowData();
    }

    public interface IWorkflowFactory<TWorkflow> where TWorkflow : Workflow
    {
        TWorkflow Create(object data);
    }

    public interface IWorkflowFactory<TWorkflow, TData> where TWorkflow : Workflow
    {
        TWorkflow Create(TData data);
    }

    public interface IWorkflowStorage<TWorkflow> where TWorkflow : Workflow
    {
        void Store(TWorkflow workflow);
        TWorkflow Load(Guid workflowId);
    }

    public interface IErrorPolicy<TWorkflow> where TWorkflow : Workflow 
    {
        bool ShouldContinue(TWorkflow workflow, Exception error);
    }

    public enum ErrorPolicy
    {
        Default,
        Throw = Default,
        StopWorkflow,
        Ignore,
    }
}
