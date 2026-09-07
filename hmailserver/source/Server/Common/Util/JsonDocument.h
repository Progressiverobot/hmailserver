// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <string>
#include <utility>
#include <vector>

namespace HM
{
   // A JSON document as a tree, read once and then asked questions.
   //
   // Until now every JSON the server reads has been searched for its keys by name
   // (AcmeClient::JsonStringValue_, RestApiServer::GetJsonStringValue_ and the like),
   // which is right for a flat object whose keys are known and wrong for anything
   // nested: a GitHub release carries an author object and an asset array whose
   // members share key names with the release itself, and a Sigstore bundle is
   // objects in arrays in objects. Those need the structure kept, so this keeps it.
   //
   // RFC 8259 as far as the readers here need: objects, arrays, strings with every
   // escape including surrogate pairs, numbers, true, false and null. Nesting is
   // capped so a hostile document cannot exhaust the stack; the size is the caller's
   // to bound before parsing. Duplicate keys keep the first, which is what a search
   // by name would have found too.
   class JsonValue
   {
   public:
      enum Type
      {
         TypeNull,
         TypeBool,
         TypeNumber,
         TypeString,
         TypeArray,
         TypeObject
      };

      JsonValue();

      Type GetType() const { return type_; }
      bool IsNull() const { return type_ == TypeNull; }
      bool IsBool() const { return type_ == TypeBool; }
      bool IsNumber() const { return type_ == TypeNumber; }
      bool IsString() const { return type_ == TypeString; }
      bool IsArray() const { return type_ == TypeArray; }
      bool IsObject() const { return type_ == TypeObject; }

      // Objects. Get answers nullptr when this is not an object or the key is absent;
      // the typed forms answer the default when the member is absent or of another type.
      const JsonValue *Get(const std::string &key) const;
      std::string GetString(const std::string &key, const std::string &defaultValue = "") const;
      bool GetBool(const std::string &key, bool defaultValue = false) const;
      __int64 GetInt64(const std::string &key, __int64 defaultValue = 0) const;
      const std::vector<std::pair<std::string, JsonValue> > &Members() const { return members_; }

      // Arrays. At answers nullptr when this is not an array or the index is past the end.
      size_t Size() const { return items_.size(); }
      const JsonValue *At(size_t index) const;
      const std::vector<JsonValue> &Items() const { return items_; }

      // Scalars. A value of another type answers the empty string, 0 or false.
      const std::string &AsString() const { return string_; }
      double AsNumber() const { return number_; }
      __int64 AsInt64() const;
      bool AsBool() const { return type_ == TypeBool && bool_; }

      // Reads text into value. False, with error saying where and why, when the text
      // is not one complete JSON value.
      static bool Parse(const std::string &text, JsonValue &value, std::string &error);

   private:
      friend class JsonParser;

      Type type_;
      bool bool_;
      double number_;
      std::string raw_number_;
      std::string string_;
      std::vector<JsonValue> items_;
      std::vector<std::pair<std::string, JsonValue> > members_;
   };
}
