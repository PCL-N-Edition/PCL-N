namespace PCL.UI.Next;

/// <summary>
/// The navigation modes the navigator reports.
/// </summary>
public enum XsrUiNavigationKind
{
    Push = 1,
    Pop = 2,
    Replace = 3,
}

/// <summary>
/// Describes one completed navigation.
/// </summary>
public readonly record struct XsrUiNavigationRecord(
    XsrUiNavigationKind Kind,
    XsrUiEntityId From,
    XsrUiEntityId To);

/// <summary>
/// Receives every completed navigation.
/// </summary>
public interface IXsrUiNavigatorObserver
{
    void OnNavigated(XsrUiNavigationRecord args);
}

/// <summary>
/// One page stack over the entity tree. The navigator owns one host entity: the current page is
/// attached under the host, back-stack pages stay alive but detached, and every transition is a
/// deterministic tree operation. Single-threaded like the tree.
/// </summary>
public sealed class XsrUiNavigator
{
    private readonly XsrUiTree _tree;
    private readonly IXsrUiNavigatorObserver? _observer;
    private readonly XsrUiEntityId _host;
    private readonly Stack<XsrUiEntityId> _back = [];
    private XsrUiEntityId _current;

    public XsrUiNavigator(XsrUiTree tree, XsrUiEntityId host, IXsrUiNavigatorObserver? observer = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        if (!tree.IsAlive(host))
        {
            throw new InvalidOperationException($"The navigation host '{host}' is not alive.");
        }

        _host = host;
        _observer = observer;
    }

    public XsrUiEntityId Current => _current;

    public int Depth => _current.IsAssigned ? _back.Count + 1 : _back.Count;

    /// <summary>
    /// Pushes one page on top of the stack and attaches it under the host. The previous page is
    /// detached but stays alive.
    /// </summary>
    public void Push(XsrUiEntityId page)
    {
        RequireAlive(page);
        if (page.Equals(_current))
        {
            throw new InvalidOperationException("The page is already the current navigation entry.");
        }

        _tree.Detach(page);
        if (_current.IsAssigned)
        {
            _tree.Detach(_current);
            _back.Push(_current);
        }

        XsrUiEntityId from = _current;
        _tree.Attach(page, _host);
        _current = page;
        Notify(new XsrUiNavigationRecord(XsrUiNavigationKind.Push, from, page));
    }

    /// <summary>
    /// Pops the current page and re-attaches the one below it. Returns false on the last page.
    /// </summary>
    public bool Pop()
    {
        if (_back.Count == 0)
        {
            return false;
        }

        XsrUiEntityId from = _current;
        _tree.Detach(_current);
        _current = _back.Pop();
        _tree.Attach(_current, _host);
        Notify(new XsrUiNavigationRecord(XsrUiNavigationKind.Pop, from, _current));
        return true;
    }

    /// <summary>
    /// Replaces the current page without changing the back stack.
    /// </summary>
    public void Replace(XsrUiEntityId page)
    {
        RequireAlive(page);
        if (!_current.IsAssigned)
        {
            Push(page);
            return;
        }

        XsrUiEntityId from = _current;
        _tree.Detach(_current);
        _tree.Detach(page);
        _tree.Attach(page, _host);
        _current = page;
        Notify(new XsrUiNavigationRecord(XsrUiNavigationKind.Replace, from, page));
    }

    private void RequireAlive(XsrUiEntityId page)
    {
        if (!_tree.IsAlive(page))
        {
            throw new InvalidOperationException($"The page '{page}' is not alive.");
        }
    }

    private void Notify(XsrUiNavigationRecord args)
    {
        _observer?.OnNavigated(args);
    }
}
