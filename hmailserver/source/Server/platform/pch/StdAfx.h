// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Shadows the server's own precompiled header on POSIX.
//
// Both spellings exist in the tree - #include "StdAfx.h" in 274 sources and
// #include "stdafx.h" in 329 - and Linux needs a file for each. Only this one is
// in the repository: a second file differing by case cannot be checked out on
// Windows, where the two names are the same file. CMake copies this to
// <build>/pch-lower/stdafx.h and puts both directories on the include path, so
// each spelling resolves and neither is checked in twice.
#pragma once
#include "../portable_stdafx.h"
