using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    class ErrorPolicyThrow<TWorkflow> : IErrorPolicy<TWorkflow> where TWorkflow : Workflow
    {
        public bool ShouldContinue(TWorkflow workflow, Exception error)
        {
            throw error;
        }
    }

    class ErrorPolicyIgnore<TWorkflow> : IErrorPolicy<TWorkflow> where TWorkflow : Workflow
    {
        public bool ShouldContinue(TWorkflow workflow, Exception error)
        {
            Console.WriteLine(
                "Ignoring " + error.GetType().Name
                + " in state " + workflow.CurrentState
                + " in workflow " + workflow.Id
            );
            return true;
        }
    }

    class ErrorPolicyStopWorkflow<TWorkflow> : IErrorPolicy<TWorkflow> where TWorkflow : Workflow
    {
        public bool ShouldContinue(TWorkflow workflow, Exception error)
        {
            Console.WriteLine(
                "Failing due to " + error.GetType().Name
                + " in state " + workflow.CurrentState
                + " in workflow " + workflow.Id
            );
            return false;
        }
    }
}
