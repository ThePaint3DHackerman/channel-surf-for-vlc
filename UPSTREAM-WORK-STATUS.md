# Source snapshot selection

The initial repository uses the complete **1.1.0.0** source from the existing `Channel Surf.zip` distribution. That snapshot successfully builds with the supplied .NET Framework compiler script.

A separate newer local source folder identifies itself as version 1.2.0.0 and contains unfinished rolling-buffer/DVR changes. Its `App.cs` references three methods that are absent: `UpdateDvrUi`, `ToggleDvrAsync`, and `RemoveBufferAsync`. Its older build script also omits three newer source files. Adding those files to the build still produces five missing-method compiler errors.

The unfinished version 1.2 work is preserved in its original local folder and is not part of this initial repository. It needs implementation and validation before replacing this complete source snapshot. This repository's version 1.1 does not offer DVR or rolling buffers.
