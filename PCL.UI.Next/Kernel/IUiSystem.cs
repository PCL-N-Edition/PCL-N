// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>One system in the fixed UI pipeline.</summary>
public interface IUiSystem
{
    UiSystemPhase Phase { get; }

    string Name { get; }

    void Update(UiWorld world, in UiFrameContext frame);
}
