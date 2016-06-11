using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    class ErrorPolicyThrow<TWorkflowData> : IErrorPolicy<TWorkflowData> where TWorkflowData : class
    {
        public bool ShouldContinue(TWorkflowData workflow, Exception error)
        {
            throw error;
        }
    }

    class ErrorPolicyIgnore<TWorkflowData> : IErrorPolicy<TWorkflowData> where TWorkflowData : class
    {
        public bool ShouldContinue(TWorkflowData workflow, Exception error)
        {
            //Console.WriteLine(
            //    "Ignoring " + error.GetType().Name
            //    + " in state " + workflow.CurrentState
            //    + " in workflow " + workflow.Id
            //);
            return true;
        }
    }

    class ErrorPolicyStopWorkflow<TWorkflowData> : IErrorPolicy<TWorkflowData> where TWorkflowData : class
    {
        public bool ShouldContinue(TWorkflowData workflow, Exception error)
        {
            //Console.WriteLine(
            //    "Failing due to " + error.GetType().Name
            //    + " in state " + workflow.CurrentState
            //    + " in workflow " + workflow.Id
            //);
            return false;
        }
    }
}
