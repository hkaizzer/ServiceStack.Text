using System;
using System.Collections.Generic;
using ServiceStack.Text.Json;

namespace ServiceStack.Text.Common
{
    internal static class DeserializeTypeRefJson
    {
        private static readonly JsonTypeSerializer Serializer = (JsonTypeSerializer)JsonTypeSerializer.Instance;

        static readonly ReadOnlyMemory<char> typeAttr = JsWriter.TypeAttr.AsMemory();

        internal static object StringToType(ReadOnlySpan<char> strType,
            TypeConfig typeConfig,
            EmptyCtorDelegate ctorFn,
            KeyValuePair<string, TypeAccessor>[] typeAccessors)
        {
            var index = 0;
            var type = typeConfig.Type;

            if (strType.IsEmpty)
                return null;

            var buffer = strType;
            var strTypeLength = strType.Length;

            //if (!Serializer.EatMapStartChar(strType, ref index))
            for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
            if (buffer[index] != JsWriter.MapStartChar)
                throw DeserializeTypeRef.CreateSerializationError(type, strType.ToString());

            index++;
            if (JsonTypeSerializer.IsEmptyMap(strType, index)) 
                return ctorFn();

            object instance = null;
            var lenient = JsConfig.PropertyConvention == PropertyConvention.Lenient;

            while (index < strTypeLength)
            {
//                var propertyName = JsonTypeSerializer.ParseJsonString(strType, ref index);
                var propertyName = JsonTypeSerializer.UnescapeJsString(strType, JsonUtils.QuoteChar, removeQuotes:true, ref index);

                //Serializer.EatMapKeySeperator(strType, ref index);
                for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
                if (strTypeLength != index) index++;

                var propertyValueStr = Serializer.EatValue(strType, ref index);
                var possibleTypeInfo = propertyValueStr != null && propertyValueStr.Length > 1;

                //if we already have an instance don't check type info, because then we will have a half deserialized object
                //we could throw here or just use the existing instance.
                if (instance == null && possibleTypeInfo && propertyName.Equals(typeAttr.Span, StringComparison.OrdinalIgnoreCase))
                {
                    var explicitTypeName = Serializer.ParseString(propertyValueStr);
                    var explicitType = JsConfig.TypeFinder(explicitTypeName);

                    if (explicitType == null || explicitType.IsInterface || explicitType.IsAbstract)
                    {
                        Tracer.Instance.WriteWarning("Could not find type: " + propertyValueStr.ToString());
                    }
                    else if (!type.IsAssignableFrom(explicitType))
                    {
                        Tracer.Instance.WriteWarning("Could not assign type: " + propertyValueStr.ToString());
                    }
                    else
                    {
                        JsWriter.AssertAllowedRuntimeType(explicitType);
                        instance = explicitType.CreateInstance();
                    }

                    if (instance != null)
                    {
                        //If __type info doesn't match, ignore it.
                        if (!type.IsInstanceOfType(instance))
                        {
                            instance = null;
                        }
                        else
                        {
                            var derivedType = instance.GetType();
                            if (derivedType != type)
                            {
                                var derivedTypeConfig = new TypeConfig(derivedType);
                                var map = DeserializeTypeRef.GetTypeAccessors(derivedTypeConfig, Serializer);
                                if (map != null)
                                {
                                    typeAccessors = map;
                                }
                            }
                        }
                    }

                    Serializer.EatItemSeperatorOrMapEndChar(strType, ref index);
                    continue;
                }

                if (instance == null) instance = ctorFn();

                var typeAccessor = typeAccessors.Get(propertyName, lenient);

                var propType = possibleTypeInfo && propertyValueStr[0] == '_' ? TypeAccessor.ExtractType(Serializer, propertyValueStr) : null;
                if (propType != null)
                {
                    try
                    {
                        if (typeAccessor != null)
                        {
                            //var parseFn = Serializer.GetParseFn(propType);
                            var parseFn = JsonReader.GetParseStringSegmentFn(propType);

                            var propertyValue = parseFn(propertyValueStr);
                            if (typeConfig.OnDeserializing != null)
                                propertyValue = typeConfig.OnDeserializing(instance, propertyName.ToString(), propertyValue);
                            typeAccessor.SetProperty(instance, propertyValue);
                        }

                        //Serializer.EatItemSeperatorOrMapEndChar(strType, ref index);
                        for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
                        if (index != strTypeLength)
                        {
                            var success = buffer[index] == JsWriter.ItemSeperator || buffer[index] == JsWriter.MapEndChar;
                            index++;
                            if (success)
                                for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
                        }

                        continue;
                    }
                    catch (Exception e)
                    {
                        if (JsConfig.OnDeserializationError != null) JsConfig.OnDeserializationError(instance, propType, propertyName.ToString(), propertyValueStr.ToString(), e);
                        if (JsConfig.ThrowOnError) throw DeserializeTypeRef.GetSerializationException(propertyName.ToString(), propertyValueStr.ToString(), propType, e);
                        else Tracer.Instance.WriteWarning("WARN: failed to set dynamic property {0} with: {1}", propertyName.ToString(), propertyValueStr.ToString());
                    }
                }

                if (typeAccessor?.GetProperty != null && typeAccessor.SetProperty != null)
                {
                    try
                    {
                        var propertyValue = typeAccessor.GetProperty(propertyValueStr);
                        if (typeConfig.OnDeserializing != null)
                            propertyValue = typeConfig.OnDeserializing(instance, propertyName.ToString(), propertyValue);
                        typeAccessor.SetProperty(instance, propertyValue);
                    }
                    catch (NotSupportedException) { throw; }
                    catch (Exception e)
                    {
                        if (JsConfig.OnDeserializationError != null) JsConfig.OnDeserializationError(instance, propType ?? typeAccessor.PropertyType, propertyName.ToString(), propertyValueStr.ToString(), e);
                        if (JsConfig.ThrowOnError) throw DeserializeTypeRef.GetSerializationException(propertyName.ToString(), propertyValueStr.ToString(), typeAccessor.PropertyType, e);
                        else Tracer.Instance.WriteWarning("WARN: failed to set property {0} with: {1}", propertyName.ToString(), propertyValueStr.ToString());
                    }
                }
                else
                {
                    // the property is not known by the DTO
                    typeConfig.OnDeserializing?.Invoke(instance, propertyName.ToString(), propertyValueStr.ToString());
                }

                //Serializer.EatItemSeperatorOrMapEndChar(strType, ref index);
                for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
                if (index != strType.Length)
                {
                    var success = buffer[index] == JsWriter.ItemSeperator || buffer[index] == JsWriter.MapEndChar;
                    index++;
                    if (success)
                        for (; index < strTypeLength; index++) { if (!JsonUtils.IsWhiteSpace(buffer[index])) break; } //Whitespace inline
                }

            }

            return instance;
        }
    }
}
