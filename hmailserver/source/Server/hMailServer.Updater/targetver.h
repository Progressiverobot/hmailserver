// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// Minimum supported platform: Windows 10 1607 / Server 2016, matching the
// server core (stdafx.h) and the installer's MinVersion gate.
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
