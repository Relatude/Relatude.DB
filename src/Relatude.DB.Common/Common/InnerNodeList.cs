using System.Collections;

namespace Relatude.DB.Common;

public class InnerNodes<T> : ICollection<T> {

    public void Move(Guid id, int newIndex) => throw new NotImplementedException();
    public void MoveRelative(Guid id, int offset) => throw new NotImplementedException();
    public int IndexOf(Guid id) => throw new NotImplementedException();

    public int Count => throw new NotImplementedException();
    public bool IsReadOnly => throw new NotImplementedException();
    public void Add(T item) => throw new NotImplementedException();
    public void Clear() => throw new NotImplementedException();
    public bool Contains(T item) => throw new NotImplementedException();
    public void CopyTo(T[] array, int arrayIndex) => throw new NotImplementedException();
    public IEnumerator<T> GetEnumerator() => throw new NotImplementedException();
    public bool Remove(T item) => throw new NotImplementedException();
    IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();

    static public InnerNodes<T> Empty { get; } = new InnerNodes<T>();
}

