using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyFlow
{
    // TODO: events -- Transitioned, Completed, Faulted, ...
    public class Workflow<TData> where TData : class
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

        public TData Data
        {
            get;
            internal set;
        } = null;
        

        public virtual void OnBeginStep() { }

        internal Workflow(Guid id, IState initialState, TData data) {
            Id = id;
            CurrentState = initialState;
            Data = data;
        }

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

            var other = obj as Workflow<TData>;
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
