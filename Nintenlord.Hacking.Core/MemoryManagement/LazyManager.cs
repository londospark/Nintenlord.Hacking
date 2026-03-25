namespace Nintenlord.Hacking.Core.MemoryManagement
{
    public class Lazy : IMemoryManager
    {
        #region IMemoryManager Members

        public ManagedPointer Reserve(int offset, int size) => new(offset, size, true);

        public void Pin(ManagedPointer ptr)
        {

        }

        public void Unpin(ManagedPointer ptr)
        {

        }

        #endregion

        #region IAllocator<ManagedPointer> Members

        public ManagedPointer Allocate(int size) => ManagedPointer.NullPointer;

        public ManagedPointer Allocate(int size, int padding) => ManagedPointer.NullPointer;

        public void Deallocate(ManagedPointer pointer)
        {

        }

        public bool IsAllocated(ManagedPointer pointer) => false;

        #endregion
    }
}
