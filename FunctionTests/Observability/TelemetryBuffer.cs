using System.Collections;
using System.Collections.Concurrent;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

public sealed class TelemetryBuffer<T> : ICollection<T>
{
    private readonly ConcurrentQueue<T> items = new();
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(T item) => items.Enqueue(item);
    public void Clear() => items.Clear();
    public bool Contains(T item) => items.Contains(item);
    public void CopyTo(T[] array, int index) => items.CopyTo(array, index);
    public bool Remove(T item) => throw new NotSupportedException();
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items.ToArray()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
