// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// See VCard.h.

#include "StdAfx.h"
#include "VCard.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int FoldWidth = 75;

      // A PREF larger than any a client writes; "no preference stated".
      const int NoPreference = 1000000;

      bool IsSpaceOrTab(char c)
      {
         return c == ' ' || c == '\t';
      }

      AnsiString Trimmed(const AnsiString &value)
      {
         AnsiString text = value;
         text.TrimLeft();
         text.TrimRight();
         return text;
      }

      // The logical lines of the text: physical lines joined where one begins
      // with a space or a tab, that character dropped (RFC 2426 section 2.6).
      void UnfoldLines(const AnsiString &text, std::vector<AnsiString> &lines)
      {
         AnsiString current;
         bool haveCurrent = false;
         int length = text.GetLength();
         int position = 0;

         while (position < length)
         {
            int lineEnd = position;
            while (lineEnd < length && text[lineEnd] != '\n')
               lineEnd++;

            AnsiString line = text.Mid(position, lineEnd - position);
            if (!line.IsEmpty() && line[line.GetLength() - 1] == '\r')
               line = line.Mid(0, line.GetLength() - 1);
            position = lineEnd + 1;

            if (haveCurrent && !line.IsEmpty() && IsSpaceOrTab(line[0]))
            {
               current += line.Mid(1);
               continue;
            }

            if (haveCurrent)
               lines.push_back(current);
            current = line;
            haveCurrent = true;
         }

         if (haveCurrent)
            lines.push_back(current);
      }

      // [group.]NAME[;parameter[=value]]*:value. False without a name or a
      // colon outside quotes.
      bool ParseContentLine(const AnsiString &line, VCardProperty &property)
      {
         bool quoted = false;
         int colon = -1;
         for (int i = 0; i < line.GetLength(); i++)
         {
            char c = line[i];
            if (c == '"')
               quoted = !quoted;
            else if (c == ':' && !quoted)
            {
               colon = i;
               break;
            }
         }

         if (colon <= 0)
            return false;

         AnsiString head = line.Mid(0, colon);
         property.value = line.Mid(colon + 1);

         std::vector<AnsiString> parts;
         AnsiString part;
         quoted = false;
         for (int i = 0; i < head.GetLength(); i++)
         {
            char c = head[i];
            if (c == '"')
            {
               quoted = !quoted;
               part += c;
            }
            else if (c == ';' && !quoted)
            {
               parts.push_back(part);
               part = "";
            }
            else
               part += c;
         }
         parts.push_back(part);

         AnsiString name = Trimmed(parts[0]);
         int dot = name.Find(".");
         if (dot >= 0)
         {
            property.group = name.Mid(0, dot);
            name = name.Mid(dot + 1);
         }
         name.ToUpper();
         if (name.IsEmpty())
            return false;
         property.name = name;

         for (size_t i = 1; i < parts.size(); i++)
         {
            AnsiString parameter = Trimmed(parts[i]);
            if (parameter.IsEmpty())
               continue;

            AnsiString parameterName;
            AnsiString parameterValue;
            int equals = parameter.Find("=");
            if (equals < 0)
            {
               // vCard 2.1 wrote EMAIL;INTERNET;PREF: - a bare word is a type.
               parameterName = "TYPE";
               parameterValue = parameter;
            }
            else
            {
               parameterName = parameter.Mid(0, equals);
               parameterValue = parameter.Mid(equals + 1);
            }

            parameterName = Trimmed(parameterName);
            parameterName.ToUpper();
            parameterValue = Trimmed(parameterValue);
            parameterValue.Remove('"');
            property.parameters.push_back(std::make_pair(parameterName, parameterValue));
         }

         return true;
      }
   }

   bool
   VCardProperty::HasParameter(const AnsiString &parameterName, const AnsiString &parameterValue) const
   {
      for (size_t i = 0; i < parameters.size(); i++)
      {
         if (parameters[i].first.CompareNoCase(parameterName) != 0)
            continue;

         std::vector<AnsiString> items = StringParser::SplitString(parameters[i].second, ",");
         for (size_t j = 0; j < items.size(); j++)
         {
            if (Trimmed(items[j]).CompareNoCase(parameterValue) == 0)
               return true;
         }
      }

      return false;
   }

   AnsiString
   VCardProperty::Parameter(const AnsiString &parameterName) const
   {
      for (size_t i = 0; i < parameters.size(); i++)
      {
         if (parameters[i].first.CompareNoCase(parameterName) == 0)
            return parameters[i].second;
      }

      return "";
   }

   bool
   VCard::Parse(const AnsiString &text, std::vector<VCardProperty> &properties, AnsiString &problem)
   {
      properties.clear();
      problem = "";

      std::vector<AnsiString> lines;
      UnfoldLines(text, lines);

      bool opened = false;
      bool closed = false;
      for (size_t i = 0; i < lines.size(); i++)
      {
         if (Trimmed(lines[i]).IsEmpty())
            continue;

         VCardProperty property;
         if (!ParseContentLine(lines[i], property))
         {
            problem = "a content line has no property name before its colon";
            return false;
         }

         AnsiString upperValue = Trimmed(property.value);
         upperValue.ToUpper();

         if (!opened)
         {
            if (property.name == "BEGIN" && upperValue == "VCARD")
            {
               opened = true;
               continue;
            }

            problem = "the card does not begin with BEGIN:VCARD";
            return false;
         }

         if (closed)
         {
            problem = "there is content after END:VCARD; a resource holds one card";
            return false;
         }

         if (property.name == "END" && upperValue == "VCARD")
         {
            closed = true;
            continue;
         }

         if (property.name == "BEGIN")
         {
            problem = "a BEGIN inside the card; a resource holds one card";
            return false;
         }

         properties.push_back(property);
      }

      if (!opened)
      {
         problem = "the card does not begin with BEGIN:VCARD";
         return false;
      }

      if (!closed)
      {
         problem = "the card does not end with END:VCARD";
         return false;
      }

      return true;
   }

   AnsiString
   VCard::Unescape(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength());

      for (int i = 0; i < value.GetLength(); i++)
      {
         char c = value[i];
         if (c == '\\' && i + 1 < value.GetLength())
         {
            char next = value[i + 1];
            if (next == 'n' || next == 'N')
            {
               result += '\n';
               i++;
               continue;
            }
            if (next == ',' || next == ';' || next == '\\')
            {
               result += next;
               i++;
               continue;
            }
         }

         result += c;
      }

      return result;
   }

   AnsiString
   VCard::Escape(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char c = value[i];
         switch (c)
         {
         case '\\': result += "\\\\"; break;
         case '\n': result += "\\n"; break;
         case '\r': break;
         case ',':  result += "\\,"; break;
         case ';':  result += "\\;"; break;
         default:   result += c; break;
         }
      }

      return result;
   }

   std::vector<AnsiString>
   VCard::Components(const AnsiString &value)
   {
      std::vector<AnsiString> components;
      AnsiString current;

      for (int i = 0; i < value.GetLength(); i++)
      {
         char c = value[i];
         if (c == '\\' && i + 1 < value.GetLength())
         {
            current += c;
            current += value[i + 1];
            i++;
            continue;
         }

         if (c == ';')
         {
            components.push_back(Unescape(current));
            current = "";
            continue;
         }

         current += c;
      }

      components.push_back(Unescape(current));
      return components;
   }

   AnsiString
   VCard::Fold(const AnsiString &line)
   {
      AnsiString result;
      result.reserve(line.GetLength() + line.GetLength() / FoldWidth * 3 + 3);

      int lineOctets = 0;
      for (int i = 0; i < line.GetLength(); i++)
      {
         unsigned char c = static_cast<unsigned char>(line[i]);

         // Break before a character, never inside one: a UTF-8 continuation
         // byte (10xxxxxx) stays with the bytes before it.
         bool continuation = (c & 0xc0) == 0x80;
         if (lineOctets >= FoldWidth && !continuation)
         {
            result += "\r\n ";
            lineOctets = 1;
         }

         result += static_cast<char>(c);
         lineOctets++;
      }

      return result;
   }

   void
   VCard::SplitName(const AnsiString &name, AnsiString &family, AnsiString &given)
   {
      AnsiString text = Trimmed(name);
      family = "";
      given = "";

      int comma = text.Find(",");
      if (comma >= 0)
      {
         family = Trimmed(text.Mid(0, comma));
         given = Trimmed(text.Mid(comma + 1));
         return;
      }

      int space = text.ReverseFind(' ');
      if (space < 0)
      {
         given = text;
         return;
      }

      family = Trimmed(text.Mid(space + 1));
      given = Trimmed(text.Mid(0, space));
   }

   bool
   VCard::ExtractNameAndAddress(const std::vector<VCardProperty> &properties, AnsiString &name, AnsiString &address)
   {
      name = "";
      address = "";

      const VCardProperty *formattedName = nullptr;
      const VCardProperty *structuredName = nullptr;
      const VCardProperty *chosenEmail = nullptr;
      int chosenPreference = NoPreference;
      bool chosenIsPreferred = false;

      for (size_t i = 0; i < properties.size(); i++)
      {
         const VCardProperty &property = properties[i];

         if (property.name == "FN")
         {
            if (!formattedName)
               formattedName = &property;
         }
         else if (property.name == "N")
         {
            if (!structuredName)
               structuredName = &property;
         }
         else if (property.name == "EMAIL")
         {
            if (Trimmed(Unescape(property.value)).IsEmpty())
               continue;

            bool preferred = false;
            int preference = NoPreference;
            if (property.HasParameter("TYPE", "PREF"))
            {
               preferred = true;
               preference = 0;
            }

            AnsiString prefValue = property.Parameter("PREF");
            if (!prefValue.IsEmpty())
            {
               preferred = true;
               int parsed = atoi(prefValue.c_str());
               preference = parsed < 1 ? 1 : parsed;
            }

            if (!chosenEmail || (preferred && (!chosenIsPreferred || preference < chosenPreference)))
            {
               chosenEmail = &property;
               chosenPreference = preference;
               chosenIsPreferred = preferred;
            }
         }
      }

      if (formattedName)
         name = Trimmed(Unescape(formattedName->value));

      if (name.IsEmpty() && structuredName)
      {
         // family;given;additional;prefix;suffix -> "given additional family"
         std::vector<AnsiString> components = Components(structuredName->value);
         const size_t order[] = { 1, 2, 0 };
         AnsiString assembled;
         for (size_t i = 0; i < 3; i++)
         {
            if (components.size() <= order[i])
               continue;

            AnsiString component = Trimmed(components[order[i]]);
            if (component.IsEmpty())
               continue;

            if (!assembled.IsEmpty())
               assembled += " ";
            assembled += component;
         }
         name = assembled;
      }

      if (!chosenEmail)
         return false;

      address = Trimmed(Unescape(chosenEmail->value));
      AnsiString lower = address;
      lower.ToLower();
      if (lower.StartsWith("mailto:"))
         address = address.Mid(7);
      address = Trimmed(address);
      address.ToLower();

      return !address.IsEmpty();
   }

   AnsiString
   VCard::Generate(const AnsiString &uid, const AnsiString &name, const AnsiString &address)
   {
      AnsiString family;
      AnsiString given;
      SplitName(name, family, given);

      AnsiString formattedName = Trimmed(name).IsEmpty() ? address : Trimmed(name);

      AnsiString card;
      card += "BEGIN:VCARD\r\n";
      card += "VERSION:3.0\r\n";
      card += "PRODID:-//hMailServer//CardDAV//EN\r\n";
      card += Fold("UID:" + uid) + "\r\n";
      card += Fold("FN:" + Escape(formattedName)) + "\r\n";
      card += Fold("N:" + Escape(family) + ";" + Escape(given) + ";;;") + "\r\n";
      card += Fold("EMAIL;TYPE=INTERNET,PREF:" + Escape(address)) + "\r\n";
      card += "END:VCARD\r\n";
      return card;
   }

   AnsiString
   VCard::Serialize(const std::vector<VCardProperty> &properties)
   {
      AnsiString card = "BEGIN:VCARD\r\n";

      // VERSION comes straight after BEGIN in both 3.0 (RFC 2426 section 3.6.9)
      // and 4.0 (RFC 6350 section 6.7.9), wherever the client had it.
      const VCardProperty *version = nullptr;
      for (size_t i = 0; !version && i < properties.size(); i++)
         if (properties[i].name == "VERSION")
            version = &properties[i];

      card += Fold("VERSION:" + (version ? Trimmed(version->value) : AnsiString("3.0"))) + "\r\n";

      for (size_t i = 0; i < properties.size(); i++)
      {
         const VCardProperty &property = properties[i];
         if (&property == version)
            continue;

         AnsiString line = property.group.IsEmpty() ? property.name : property.group + "." + property.name;
         for (size_t j = 0; j < property.parameters.size(); j++)
         {
            const AnsiString &value = property.parameters[j].second;
            // The parser took the quotes off; a value with a delimiter in it gets them back.
            bool quote = value.Find(";") >= 0 || value.Find(":") >= 0 || value.Find(",") >= 0;
            line += ";" + property.parameters[j].first + "=" + (quote ? "\"" + value + "\"" : value);
         }
         line += ":" + property.value;
         card += Fold(line) + "\r\n";
      }

      card += "END:VCARD\r\n";
      return card;
   }

   AnsiString
   VCard::UidOf(const std::vector<VCardProperty> &properties)
   {
      for (size_t i = 0; i < properties.size(); i++)
         if (properties[i].name == "UID")
            return Trimmed(Unescape(properties[i].value));
      return "";
   }

   bool
   VCard::UsesLegacyEncoding(const std::vector<VCardProperty> &properties, AnsiString &which)
   {
      for (size_t i = 0; i < properties.size(); i++)
      {
         const VCardProperty &property = properties[i];
         for (size_t j = 0; j < property.parameters.size(); j++)
         {
            AnsiString value = property.parameters[j].second;
            value.ToUpper();
            if (property.parameters[j].first == "ENCODING" && value == "QUOTED-PRINTABLE")
            {
               which = property.name + ";ENCODING=QUOTED-PRINTABLE";
               return true;
            }
            if (property.parameters[j].first == "CHARSET")
            {
               which = property.name + ";CHARSET=" + property.parameters[j].second;
               return true;
            }
         }
      }
      return false;
   }

   AnsiString
   VCard::WithNameAndAddress(const AnsiString &card, const AnsiString &uid, const AnsiString &name, const AnsiString &address)
   {
      std::vector<VCardProperty> properties;
      AnsiString problem;
      if (!Parse(card, properties, problem))
         return Generate(uid, name, address);

      AnsiString family;
      AnsiString given;
      SplitName(name, family, given);
      AnsiString formattedName = Trimmed(name).IsEmpty() ? address : Trimmed(name);

      VCardProperty *fn = nullptr;
      VCardProperty *n = nullptr;
      VCardProperty *email = nullptr;
      bool emailPreferred = false;
      int emailPreference = NoPreference;
      bool hasUid = false;

      for (size_t i = 0; i < properties.size(); i++)
      {
         VCardProperty &property = properties[i];
         if (property.name == "FN" && !fn)
            fn = &property;
         else if (property.name == "N" && !n)
            n = &property;
         else if (property.name == "UID")
            hasUid = true;
         else if (property.name == "EMAIL")
         {
            // The same choice ExtractNameAndAddress makes, so the address the
            // row holds is the one the card's preferred EMAIL carries.
            bool preferred = false;
            int preference = NoPreference;
            if (property.HasParameter("TYPE", "PREF"))
            {
               preferred = true;
               preference = 0;
            }
            AnsiString prefValue = property.Parameter("PREF");
            if (!prefValue.IsEmpty())
            {
               preferred = true;
               int parsed = atoi(prefValue.c_str());
               preference = parsed < 1 ? 1 : parsed;
            }
            if (!email || (preferred && (!emailPreferred || preference < emailPreference)))
            {
               email = &property;
               emailPreference = preference;
               emailPreferred = preferred;
            }
         }
      }

      if (fn)
         fn->value = Escape(formattedName);
      if (n)
         n->value = Escape(family) + ";" + Escape(given) + ";;;";
      if (email)
         email->value = Escape(address);

      if (!fn)
      {
         VCardProperty property;
         property.name = "FN";
         property.value = Escape(formattedName);
         properties.push_back(property);
      }
      if (!n)
      {
         VCardProperty property;
         property.name = "N";
         property.value = Escape(family) + ";" + Escape(given) + ";;;";
         properties.push_back(property);
      }
      if (!email)
      {
         VCardProperty property;
         property.name = "EMAIL";
         property.parameters.push_back(std::make_pair(AnsiString("TYPE"), AnsiString("INTERNET,PREF")));
         property.value = Escape(address);
         properties.push_back(property);
      }
      if (!hasUid && !uid.IsEmpty())
      {
         VCardProperty property;
         property.name = "UID";
         property.value = uid;
         properties.push_back(property);
      }

      return Serialize(properties);
   }
}
