using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    public abstract class Workflow
    {
        public IState CurrentState
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

        public override string ToString()
        {
            string status;
            if (IsComplete)
            {
                status = "Complete";
            }
            else if (IsFaulted)
            {
                status = "Faulted";
            }
            else
            {
                status = "Active";
            }

            return CurrentState + $" [{status}]";
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            var other = obj as Workflow;
            if (other == null)
            {
                return false;
            }

            return Id.Equals(other.Id);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
