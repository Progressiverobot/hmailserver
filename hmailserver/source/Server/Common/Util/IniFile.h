// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The Win32 profile API, for the POSIX build.
//
// hMailServer's entire configuration - the database it connects to, the
// administrator's password, the directories it works in, the LDAP binding, the
// rate limits, the REST API keys - is read and written through
// GetPrivateProfileString and its three companions, and has been since the
// first version. Those four functions live in kernel32 and have no POSIX
// equivalent, so this header declares them under their own names and
// Common/Util/IniFile.cpp implements them over hMailServer.INI itself.
//
// Nothing here is compiled on Windows. The whole file is behind
// HM_PLATFORM_POSIX, so the Windows build reaches <windows.h> for these calls
// exactly as it always has and cannot see a line of it.
//
// Which Win32 behaviours this reproduces - and, just as importantly, which it
// deliberately does not - is set out at the top of IniFile.cpp. Read that
// before relying on a corner of the profile API that this tree does not
// already use.
#pragma once

#ifdef HM_PLATFORM_POSIX

// The signatures are Win32's, argument for argument, because these are the
// calls the sources already make: the same order, the same return types, and
// the same use of a null pointer to mean "delete this" or "enumerate that".
// They are declared in the global namespace, unadorned, for the same reason -
// the call sites are unchanged.

// Copies the value of one key into a caller's buffer, or the caller's default
// when the key is not there. Returns the number of characters copied, not
// counting the terminating null.
//
// section == nullptr enumerates the section names; key == nullptr enumerates
// the key names of one section. Both forms write a run of null-terminated
// names followed by one further null, and both are described in IniFile.cpp.
DWORD GetPrivateProfileString(LPCWSTR section, LPCWSTR key, LPCWSTR defaultValue,
                              LPWSTR buffer, DWORD size, LPCWSTR fileName);

// The same lookup, read as an unsigned decimal number. A missing key gives the
// caller's default; a key that is present but does not begin with a digit
// gives 0, which is NOT the same thing and which this tree depends on.
UINT GetPrivateProfileInt(LPCWSTR section, LPCWSTR key, INT defaultValue, LPCWSTR fileName);

// Copies a whole section as "key=value" entries, each terminated by a null,
// the run terminated by one further null. Returns the number of characters
// copied, not counting that last null, or size - 2 when the buffer was too
// small - which is how a caller knows to grow it and try again.
DWORD GetPrivateProfileSection(LPCWSTR section, LPWSTR buffer, DWORD size, LPCWSTR fileName);

// Writes one value, creating the file and the section if either is absent.
//
//   key == nullptr    deletes the whole section.
//   value == nullptr  deletes just that key - which is not the same as writing
//                     an empty value, because an empty value reads back as 0
//                     from GetPrivateProfileInt rather than as the default.
//   section == nullptr is Win32's "flush the cache" call. There is no cache
//                     here, so it succeeds and does nothing; see IniFile.cpp.
//
// Returns TRUE on success and FALSE on failure, as Win32 does.
BOOL WritePrivateProfileString(LPCWSTR section, LPCWSTR key, LPCWSTR value, LPCWSTR fileName);

#endif
