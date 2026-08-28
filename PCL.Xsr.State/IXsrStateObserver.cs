namespace PCL.Xsr.State;

/// <summary>
/// Receives every applied state change. The store never lets an observer failure affect publication.
/// </summary>
public interface IXsrStateObserver
{
    void OnChanged(XsrStateChange change);
}
