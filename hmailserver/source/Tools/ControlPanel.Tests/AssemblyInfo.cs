// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// The Control Panel's service layer now calls Loc.L() and Loc.F() on the way to
// every caption, and LocTests installs catalogues into that one static seam.
// Two test classes running at once would therefore read each other's
// catalogue - a throwing one, a Swedish one - so the assembly runs its tests
// one at a time. The whole suite takes a few seconds either way.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
