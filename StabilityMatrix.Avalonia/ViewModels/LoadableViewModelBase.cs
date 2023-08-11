﻿using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NLog;
using StabilityMatrix.Avalonia.Models;

namespace StabilityMatrix.Avalonia.ViewModels;

public abstract class LoadableViewModelBase : ViewModelBase, IJsonLoadableState
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    
    // ReSharper disable once MemberCanBePrivate.Global
    protected static readonly Type[] SerializerIgnoredTypes =
    {
        typeof(ICommand),
        typeof(IRelayCommand)
    };
    
    // ReSharper disable once MemberCanBePrivate.Global
    protected static readonly string[] SerializerIgnoredNames =
    {
        nameof(HasErrors),
    };
    
    protected static readonly JsonSerializerOptions SerializerOptions = new()
    {
        IgnoreReadOnlyProperties = true,
    };

    private static bool ShouldIgnoreProperty(PropertyInfo property)
    {
        // Skip if read-only and not IJsonLoadableState
        if (property.SetMethod is null && !typeof(IJsonLoadableState).IsAssignableFrom(property.PropertyType))
        {
            Logger.Trace("Skipping {Property} - read-only", property.Name);
            return true;
        }
        // Check not JsonIgnore
        if (property.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0)
        {
            Logger.Trace("Skipping {Property} - has [JsonIgnore]", property.Name);
            return true;
        }
        // Check not excluded type
        if (SerializerIgnoredTypes.Contains(property.PropertyType))
        {
            Logger.Trace("Skipping {Property} - serializer ignored type {Type}", property.Name, property.PropertyType);
            return true;
        }
        // Check not ignored name
        if (SerializerIgnoredNames.Contains(property.Name, StringComparer.Ordinal))
        {
            Logger.Trace("Skipping {Property} - serializer ignored name", property.Name);
            return true;
        }

        return false;
    }
    
    /// <summary>
    /// Load the state of this view model from a JSON object.
    /// The default implementation is a mirror of <see cref="SaveStateToJsonObject"/>.
    /// For the following properties on this class, we will try to set from the JSON object:
    /// <list type="bullet">
    /// <item>Public</item>
    /// <item>Not read-only</item>
    /// <item>Not marked with [JsonIgnore]</item>
    /// <item>Not a type within the SerializerIgnoredTypes</item>
    /// <item>Not a name within the SerializerIgnoredNames</item>
    /// </list>
    /// </summary>
    public virtual void LoadStateFromJsonObject(JsonObject state)
    {
        // Get all of our properties using reflection
        var properties = GetType().GetProperties();
        Logger.Trace("Serializing {Type} with {Count} properties", GetType(), properties.Length);

        foreach (var property in properties)
        {
            // Check if property is in the JSON object
            if (!state.TryGetPropertyValue(property.Name, out var value))
            {
                Logger.Trace("Skipping {Property} - not in JSON object", property.Name);
                continue;
            }
            
            // Check if we should ignore this property
            if (ShouldIgnoreProperty(property))
            {
                continue;
            }
            
            // For types that also implement IJsonLoadableState, defer to their load implementation
            if (typeof(IJsonLoadableState).IsAssignableFrom(property.PropertyType))
            {
                Logger.Trace("Loading {Property} ({Type}) with IJsonLoadableState", property.Name, property.PropertyType);
                
                // Value must be non-null
                if (value is null)
                {
                    throw new InvalidOperationException($"Property {property.Name} is IJsonLoadableState but value to be loaded is null");
                }
                
                // Check if the current object at this property is null
                if (property.GetValue(this) is not IJsonLoadableState propertyValue)
                {
                    // If null, it must have a default constructor
                    if (property.PropertyType.GetConstructor(Type.EmptyTypes) is not { } constructorInfo)
                    {
                        throw new InvalidOperationException($"Property {property.Name} is IJsonLoadableState but current object is null and has no default constructor");
                    }
                    
                    // Create a new instance and set it
                    propertyValue = (IJsonLoadableState) constructorInfo.Invoke(null);
                    property.SetValue(this, propertyValue);
                }
                
                // Load the state from the JSON object
                propertyValue.LoadStateFromJsonObject(value.AsObject());
            }
            else
            {
                Logger.Trace("Loading {Property} ({Type})", property.Name, property.PropertyType);
                
                var propertyValue = value.Deserialize(property.PropertyType, SerializerOptions);
                property.SetValue(this, propertyValue);
            }
        }
    }

    /// <summary>
    /// Saves the state of this view model to a JSON object.
    /// The default implementation uses reflection to
    /// save all properties that are:
    /// <list type="bullet">
    /// <item>Public</item>
    /// <item>Not read-only</item>
    /// <item>Not marked with [JsonIgnore]</item>
    /// <item>Not a type within the SerializerIgnoredTypes</item>
    /// <item>Not a name within the SerializerIgnoredNames</item>
    /// </list>
    /// </summary>
    public virtual JsonObject SaveStateToJsonObject()
    {
        // Get all of our properties using reflection.
        var properties = GetType().GetProperties();
        Logger.Trace("Serializing {Type} with {Count} properties", GetType(), properties.Length);
        
        // Create a JSON object to store the state.
        var state = new JsonObject();
        
        // Serialize each property marked with JsonIncludeAttribute.
        foreach (var property in properties)
        {
            if (ShouldIgnoreProperty(property))
            {
                continue;
            }

            // For types that also implement IJsonLoadableState, defer to their implementation.
            if (typeof(IJsonLoadableState).IsAssignableFrom(property.PropertyType))
            {
                Logger.Trace("Serializing {Property} ({Type}) with IJsonLoadableState", property.Name, property.PropertyType);
                var value = property.GetValue(this);
                if (value is not null)
                {
                    var model = (IJsonLoadableState) value;
                    var modelState = model.SaveStateToJsonObject();
                    state.Add(property.Name, modelState);
                }
            }
            else
            {
                Logger.Trace("Serializing {Property} ({Type})", property.Name, property.PropertyType);
                var value = property.GetValue(this);
                if (value is not null)
                {
                    state.Add(property.Name, JsonSerializer.SerializeToNode(value, SerializerOptions));
                }
            }
        }
        
        return state;
    }
    
    /// <summary>
    /// Serialize a model to a JSON object.
    /// </summary>
    protected static JsonObject SerializeModel<T>(T model)
    {
        var node = JsonSerializer.SerializeToNode(model);
        return node?.AsObject() ?? throw new 
            NullReferenceException("Failed to serialize state to JSON object.");
    }
    
    /// <summary>
    /// Deserialize a model from a JSON object.
    /// </summary>
    protected static T DeserializeModel<T>(JsonObject state)
    {
        return state.Deserialize<T>() ?? throw new 
            NullReferenceException("Failed to deserialize state from JSON object.");
    }
}
