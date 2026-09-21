
namespace VisualHFT.Commons.Pools
{
    public class RollingWindow<T>
    {
        private readonly Queue<T> _queue = new Queue<T>();
        private readonly int _maxSize;

        public RollingWindow(int size)
        {
            _maxSize = size;
        }

        public void Add(T item)
        {
            _queue.Enqueue(item);
            if (_queue.Count > _maxSize)
                _queue.Dequeue();
        }

        /// <summary>
        /// Adds an item and returns the evicted item if the window was at capacity.
        /// </summary>
        /// <param name="item">Item to add</param>
        /// <param name="evicted">The item that was evicted, if any</param>
        /// <returns>True if an item was evicted</returns>
        public bool AddWithEviction(T item, out T evicted)
        {
            bool willEvict = _queue.Count >= _maxSize;
            evicted = willEvict ? _queue.Dequeue() : default!;
            _queue.Enqueue(item);
            return willEvict;
        }

        public IEnumerable<T> Items => _queue;

        public int Count => _queue.Count;

        public void Clear()
        {
            _queue.Clear();
        }

        public T Last()
        {
            return _queue.Last();
        }

        public bool Any()
        {
            return _queue.Any();
        }
    }
}
