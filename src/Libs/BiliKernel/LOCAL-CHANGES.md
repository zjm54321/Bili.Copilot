# Vendored BiliKernel SDK

This is the source snapshot that produced the local
`0.0.2-preview2-bilicopilot.2611.2.sb2` candidate packages on 2026-10-11.
Upstream: https://github.com/Richasy/bili-kernel, public package baseline
`0.0.2-preview2`. The supplied snapshot has no Git metadata, so an upstream
commit ID cannot be recovered from it. Its original README and GPLv3 LICENSE
are retained verbatim. The historical NuGet metadata says MIT; the included
source LICENSE is GPLv3 and takes precedence when distributing this source.

Only Abstractions and Services.Comment/Media/Moment were supplied/modified;
Core and all other SDK modules remain public NuGet dependencies. The local
version adds the consumer's comment/media/moment contracts and implementations
and pins the service packages to the matching Abstractions build. It must not
be replaced by the public preview package: that loses these API changes.

The snapshot is included because its four local package versions do not exist
on the configured public feeds. `scripts/Build-BiliKernel.ps1` reconstructs
them in dependency order, with their original package and assembly versions,
using only repository NuGet sources. No bin/obj or cached packages are included.
The build omits the consumer repository's Git revision from informational
versions, matching the original non-Git snapshot. Source paths/debug records
can differ after relocation; see the release verification record for IL checks.
