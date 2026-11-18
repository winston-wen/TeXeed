using TeXeed.Core;

namespace TeXeed.App;

public sealed record PreviewOptions(double Zoom, bool AutoWidth, PreviewFollowMode Follow, bool Invert, bool Crop,
    bool CompressWhitespace = true, double MaximumWidthCm = 30);
