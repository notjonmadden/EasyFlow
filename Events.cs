using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    public class WorkflowEventArgs<TWorkflowData> : EventArgs where TWorkflowData : class
    {
        public Workflow<TWorkflowData> Workflow
        {
            get;
            private set;
        }

        public WorkflowEventArgs(Workflow<TWorkflowData> workflow)
        {
            Workflow = workflow;
        }
    }

    public class WorkflowTransitionedEventArgs<TWorkflowData> : WorkflowEventArgs<TWorkflowData> where TWorkflowData : class
    {
        public ITransition Transition
        {
            get;
            private set;
        }

        public WorkflowTransitionedEventArgs(ITransition transition, Workflow<TWorkflowData> workflow)
            : base(workflow)
        {
            Transition = transition;
        }
    }

    public class WorkflowFailedEventArgs<TWorkflowData> : WorkflowEventArgs<TWorkflowData> where TWorkflowData : class
    {
        public Exception Exception
        {
            get;
            private set;
        }

        public WorkflowFailedEventArgs(Exception exception, Workflow<TWorkflowData> workflow)
            : base(workflow)
        {
            Exception = exception;
        }
    }
}
