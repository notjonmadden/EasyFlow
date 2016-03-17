using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    public class WorkflowEventArgs : EventArgs
    {
        public Workflow Workflow
        {
            get;
            private set;
        }

        public WorkflowEventArgs(Workflow workflow)
        {
            Workflow = workflow;
        }
    }

    public class WorkflowTransitionedEventArgs : WorkflowEventArgs
    {
        public ITransition Transition
        {
            get;
            private set;
        }

        public WorkflowTransitionedEventArgs(ITransition transition, Workflow workflow)
            : base(workflow)
        {
            Transition = transition;
        }
    }

    public class WorkflowFailedEventArgs : WorkflowEventArgs
    {
        public Exception Exception
        {
            get;
            private set;
        }

        public WorkflowFailedEventArgs(Exception exception, Workflow workflow)
            : base(workflow)
        {
            Exception = exception;
        }
    }
}
