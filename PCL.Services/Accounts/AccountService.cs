using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Accounts;

/// <summary>
/// Stable error contracts of the account capability. Codes are semantic identifiers and never
/// change meaning.
/// </summary>
public static class AccountErrors
{
    public static readonly XsrSemanticId InvalidProfileCode = XsrSemanticId.Parse("accounts.invalid_profile");
    public static readonly XsrSemanticId ProfileNotFoundCode = XsrSemanticId.Parse("accounts.profile_not_found");
    public static readonly XsrSemanticId PersistFailedCode = XsrSemanticId.Parse("accounts.persist_failed");

    public static XsrError InvalidProfile(string reason) =>
        new(XsrErrorKind.Rejected, InvalidProfileCode, $"The launch profile was rejected: {reason}");

    public static XsrError ProfileNotFound(int index) =>
        new(XsrErrorKind.NotFound, ProfileNotFoundCode, $"No launch profile exists at index {index}.");

    public static XsrError PersistFailed(string reason) =>
        new(XsrErrorKind.Unavailable, PersistFailedCode, $"The launch profile store could not be written: {reason}");
}

/// <summary>
/// The account capability: the persisted launch profile list published as one ordered state
/// collection of credential-free views. Writes are durable-first — the port saves the new
/// profile set before any state is published, so Success means persisted and a failure changes
/// nothing. Credentials stay in the persistence layer and results; they never enter state.
/// </summary>
public sealed class AccountService
{
    public const string OwnerName = "PCL.Services.Accounts";

    /// <summary>
    /// The ordered collection state key: items are <see cref="LaunchProfileView"/>, keyed by
    /// list index.
    /// </summary>
    public static readonly XsrSemanticId ProfilesKey = XsrSemanticId.Parse("accounts.profiles");

    private const int MaxStateConflicts = 8;

    private readonly ILaunchProfilePort _port;
    private readonly object _gate = new();
    private readonly XsrStateStore _store;
    private readonly XsrStateId _profilesId;
    private List<LaunchProfile> _profiles;

    /// <summary>The index of the profile the product currently launches with.</summary>
    public static readonly XsrSemanticId SelectedKey = XsrSemanticId.Parse("accounts.selected");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Collection<LaunchProfileView, int>(
            ProfilesKey,
            OwnerName,
            static view => view.Index);
        builder.Cell<int>(SelectedKey, OwnerName);
    }

    public AccountService(XsrStateStore store, ILaunchProfilePort port)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _profilesId = _store.Resolve(ProfilesKey);

        List<LaunchProfile> loaded;
        try
        {
            loaded = [.. _port.Load().Profiles];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = AccountErrors.PersistFailed(failure.Message);
            loaded = [];
        }

        _profiles = loaded;
        _selectedIndex = loaded.Count > 0 ? 0 : -1;
        lock (_gate)
        {
            PublishAll();
            if (LoadError is not null)
            {
                _store.MarkAvailability(_profilesId, XsrStateAvailability.Unavailable);
            }
        }

        PublishSelection();
    }

    private int _selectedIndex = -1;

    /// <summary>
    /// The profile the product currently launches with, or -1 when the roster is empty.
    /// Selection is session state published as <see cref="SelectedKey"/>; the roster file is
    /// untouched by switching.
    /// </summary>
    public int SelectedIndex { get { lock (_gate) return _selectedIndex; } }

    /// <summary>
    /// Switches the active profile as session state. An optional roster revision rejects stale
    /// UI rows after a concurrent roster edit. Switching does not write the profile file.
    /// </summary>
    public XsrError? SelectProfile(int index, long? expectedRosterRevision = null)
    {
        lock (_gate)
        {
            if (expectedRosterRevision is { } expected
                && _store.ReadCollection<LaunchProfileView>(_profilesId).Revision != expected)
            {
                return AccountErrors.InvalidProfile("the roster changed; select the profile again.");
            }

            if (index < 0 || index >= _profiles.Count)
            {
                return AccountErrors.ProfileNotFound(index);
            }

            _selectedIndex = index;
            PublishSelection();
        }
        return null;
    }

    public XsrStateStore StateStore => _store;

    /// <summary>
    /// The stable error recorded when the persisted store could not be read at startup.
    /// </summary>
    public XsrError? LoadError { get; }

    /// <summary>
    /// Appends one profile and persists the whole list atomically.
    /// </summary>
    public XsrResult<int> AddProfile(LaunchProfile profile)
    {
        XsrResult validated = Validate(profile);
        if (!validated.IsSuccess)
        {
            return XsrResult.Failure<int>(validated.Error!);
        }

        lock (_gate)
        {
            List<LaunchProfile> updated = [.. _profiles, profile];
            XsrResult saved = Persist(updated);
            if (!saved.IsSuccess)
            {
                return XsrResult.Failure<int>(saved.Error!);
            }

            _profiles = updated;
            if (_selectedIndex < 0 && _profiles.Count > 0)
            {
                _selectedIndex = 0;
            }
            PublishAll();
            PublishSelection();
            return XsrResult.Success(_profiles.Count - 1);
        }
    }

    /// <summary>Imports a fully validated batch in one durable write, preserving existing identities.</summary>
    public XsrResult<int> ImportProfiles(IReadOnlyList<LaunchProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count > 256) return XsrResult.Failure<int>(AccountErrors.InvalidProfile("Too many imported profiles."));
        List<LaunchProfile> normalized = [];
        foreach (LaunchProfile profile in profiles)
        {
            XsrResult valid = Validate(profile);
            if (!valid.IsSuccess) return XsrResult.Failure<int>(valid.Error!);
            normalized.Add(profile with
            {
                Info = profile.Info ?? "",
                Uuid = profile.Uuid ?? "",
                Logo = profile.Logo ?? "",
                SvgIcon = profile.SvgIcon ?? "lucide/user",
                AuthServer = profile.AuthServer ?? "",
                AccessToken = profile.AccessToken ?? "",
                RefreshToken = profile.RefreshToken ?? "",
                ProviderAccessToken = profile.ProviderAccessToken ?? "",
                ClientToken = profile.ClientToken ?? "",
            });
        }
        lock (_gate)
        {
            List<LaunchProfile> merged = [.. _profiles];
            foreach (LaunchProfile profile in normalized)
            {
                if (!merged.Any(existing => existing.Kind == profile.Kind
                    && string.Equals(existing.AuthServer.TrimEnd('/'), profile.AuthServer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                    && (profile.Uuid.Length > 0
                        ? string.Equals(existing.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(existing.Username, profile.Username, StringComparison.OrdinalIgnoreCase))))
                    merged.Add(profile);
            }
            int added = merged.Count - _profiles.Count;
            if (added == 0) return XsrResult.Success(0);
            XsrResult saved = Persist(merged);
            if (!saved.IsSuccess) return XsrResult.Failure<int>(saved.Error!);
            _profiles = merged;
            if (_selectedIndex < 0) _selectedIndex = 0;
            PublishAll();
            PublishSelection();
            return XsrResult.Success(added);
        }
    }

    /// <summary>
    /// Replaces the profile at the given index and persists the whole list atomically.
    /// </summary>
    public XsrResult ReplaceProfile(int index, LaunchProfile profile)
    {
        XsrResult validated = Validate(profile);
        if (!validated.IsSuccess)
        {
            return validated;
        }

        lock (_gate)
        {
            if (index < 0 || index >= _profiles.Count)
            {
                return XsrResult.Failure(AccountErrors.ProfileNotFound(index));
            }

            List<LaunchProfile> updated = [.. _profiles];
            updated[index] = profile;
            XsrResult saved = Persist(updated);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            _profiles = updated;
            PublishAll();
            return XsrResult.Success();
        }
    }

    /// <summary>
    /// Removes the profile at the given index and persists the whole list atomically. Later
    /// profiles shift down; published views re-index accordingly.
    /// </summary>
    public XsrResult RemoveProfile(int index, long? expectedRosterRevision = null)
    {
        lock (_gate)
        {
            if (expectedRosterRevision is { } expected && _store.ReadCollection<LaunchProfileView>(_profilesId).Revision != expected)
                return XsrResult.Failure(AccountErrors.InvalidProfile("the roster changed; choose the profile again."));
            if (index < 0 || index >= _profiles.Count)
            {
                return XsrResult.Failure(AccountErrors.ProfileNotFound(index));
            }

            List<LaunchProfile> updated = [.. _profiles];
            updated.RemoveAt(index);
            XsrResult saved = Persist(updated);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            _profiles = updated;
            // Preserve the same selected profile when an earlier row shifts down. If the
            // selected profile was removed, choose its successor (or the final survivor).
            _selectedIndex = _profiles.Count == 0 ? -1
                : _selectedIndex > index ? _selectedIndex - 1
                : Math.Min(_selectedIndex, _profiles.Count - 1);
            PublishAll();
            PublishSelection();
            return XsrResult.Success();
        }
    }

    /// <summary>
    /// One coherent read of the published credential-free views.
    /// </summary>
    public IReadOnlyList<LaunchProfileView> GetViews() =>
        _store.ReadCollection<LaunchProfileView>(_profilesId).Items;

    /// <summary>
    /// Resolves one full launch profile inside the Services boundary. Credentials are returned
    /// only to trusted launch/account orchestration and are never published into host state.
    /// </summary>
    public XsrResult<LaunchProfile> GetProfile(int index)
    {
        lock (_gate)
        {
            return index >= 0 && index < _profiles.Count
                ? XsrResult.Success(_profiles[index])
                : XsrResult.Failure<LaunchProfile>(AccountErrors.ProfileNotFound(index));
        }
    }

    private static XsrResult Validate(LaunchProfile profile)
    {
        if (profile is null)
        {
            return XsrResult.Failure(AccountErrors.InvalidProfile("profiles cannot be null."));
        }

        if (string.IsNullOrWhiteSpace(profile.Username))
        {
            return XsrResult.Failure(AccountErrors.InvalidProfile("a username is required."));
        }

        if (!Enum.IsDefined(profile.Kind))
        {
            return XsrResult.Failure(AccountErrors.InvalidProfile("the profile kind is not defined."));
        }

        return XsrResult.Success();
    }

    private XsrResult Persist(List<LaunchProfile> profiles)
    {
        try
        {
            _port.Save(new LaunchProfileSet { Profiles = profiles });
            return XsrResult.Success();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return XsrResult.Failure(AccountErrors.PersistFailed(failure.Message));
        }
    }

    private void PublishSelection() => _store.Publish(_store.Resolve(SelectedKey), _selectedIndex);

    private void PublishAll()
    {
        lock (_gate)
        {
            for (int attempt = 0; attempt < MaxStateConflicts; attempt++)
            {
                XsrCollectionSnapshot<LaunchProfileView> snapshot = _store.ReadCollection<LaunchProfileView>(_profilesId);
                int bound = Math.Max(snapshot.Count, _profiles.Count);
                HashSet<int> kept = [.. Enumerable.Range(0, _profiles.Count)];
                List<int> removals = [.. Enumerable.Range(0, bound).Where(index => !kept.Contains(index))];
                List<LaunchProfileView> upserts = [.. Enumerable.Range(0, _profiles.Count).Select(ViewAt)];

                XsrCollectionApplyResult result = _store.PublishDelta(
                    _profilesId,
                    new XsrCollectionDelta<LaunchProfileView, int>(snapshot.Revision, upserts, removals));
                if (result.IsApplied)
                {
                    _store.MarkAvailability(_profilesId, XsrStateAvailability.Available);
                    return;
                }
            }
        }
    }

    private LaunchProfileView ViewAt(int index)
    {
        LaunchProfile profile = _profiles[index];
        return new LaunchProfileView(
            index,
            profile.Username,
            profile.Info,
            profile.Kind,
            profile.Uuid,
            profile.Logo,
            profile.SvgIcon,
            profile.SkinAddress,
            profile.AuthServer);
    }
}
