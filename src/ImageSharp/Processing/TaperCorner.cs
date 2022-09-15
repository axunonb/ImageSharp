﻿// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

namespace SixLabors.ImageSharp.Processing;

/// <summary>
/// Enumerates the various options which determine how to taper corners
/// </summary>
public enum TaperCorner
{
    /// <summary>
    /// Taper the left or top corner
    /// </summary>
    LeftOrTop,

    /// <summary>
    /// Taper the right or bottom corner
    /// </summary>
    RightOrBottom,

    /// <summary>
    /// Taper the both sets of corners
    /// </summary>
    Both
}
