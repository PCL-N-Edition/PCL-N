# XSR-725 capability planning

## Boundary

Machine collection remains a typed registry of facts. Instance facts are evaluated only when
`MachineCapabilityQuery` carries an explicit `InstanceDirectory` or `InstanceId`; an unscoped
query never chooses an instance by release date. This prevents resource estimates and launch
preflight from evaluating a different installation than the one selected by the user.

Ordinary derivations consume only `Available` inputs. `PlatformUnsupported` is an unavailable
state, not a typed zero value. A derivation that needs platform absence as data must implement
that rule explicitly instead of inheriting the default evaluator.

The planning pipeline keeps the six Registry layers separate:

1. providers collect machine and explicitly scoped instance facts;
2. derivations calculate one typed fact;
3. the estimator projection publishes the complete heap/native/resource/graphics/physical/
   commit model with immutable provenance, historical calibration and a versioned profile;
4. preflight rules create issues, then normalize, deduplicate, collapse their causal graph and
   calculate overall severity;
5. remediation IDs resolve one-to-one to typed handlers through a sealed XSR command;

Estimated evidence cannot create `Blocked`. Only a verified hard constraint may block launch.

## Compatibility

Existing unscoped capability queries continue returning machine facts; their instance facts are
unavailable until the caller supplies the selected instance.

The Avalonia host reports keyboard, pointer and touch events through a platform-neutral event;
Desktop maps that event into the session-local input tracker. Native controller bridges use the
same path through `ReportControllerInput`, so the capability layer never installs global hooks.

## Validation

- provider failures and `PlatformUnsupported` inputs both degrade derived facts to
  `DependencyMissing`;
- a selected older instance wins even when a newer instance exists;
- input usage is session-local and requires explicit host events;
- baseline estimates retain model/profile/input/margin/reason provenance;
- estimated memory pressure is Critical, while verified missing Java can be Blocked;
- all §37-49 rule IDs are projected, including inactive rules as `false`;
- unavailable Boolean facts do not trigger negative preflight rules;
- zero/unavailable observations do not enter historical P95 calibration;
- remediation dispatch rejects missing, unconfirmed and mismatched handlers;
