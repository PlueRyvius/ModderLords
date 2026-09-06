using System;
using System.Reflection;

namespace ModderLords.CompatSync;

/// <summary>One settable value, whatever holds it. Get/Set may throw; callers wrap.</summary>
public abstract class PropertyRef
{
    public abstract string Id { get; }
    public abstract Type? ValueType { get; }
    public abstract bool CanWrite { get; }
    public abstract object? Get();
    public abstract void Set(object value);
}

/// <summary>MCM's PropertyReference (duck-typed: a "Value" property and, in v5, a "Type" property).</summary>
public sealed class McmPropertyRef : PropertyRef
{
    private readonly object _reference;
    private readonly PropertyInfo? _valueProp;

    public McmPropertyRef(string id, object reference)
    {
        Id = id;
        _reference = reference;
        _valueProp = reference.GetType().GetProperty("Value");
    }

    public override string Id { get; }
    public override Type? ValueType => _reference.GetType().GetProperty("Type")?.GetValue(_reference) as Type ?? _valueProp?.PropertyType;
    public override bool CanWrite => _valueProp?.CanWrite == true;
    public override object? Get() => _valueProp?.GetValue(_reference);
    public override void Set(object value) => _valueProp?.SetValue(_reference, value);
}

/// <summary>A CLR property or field on an object (or a static one when target is null).</summary>
public sealed class ReflectionPropertyRef : PropertyRef
{
    private readonly PropertyInfo? _prop;
    private readonly FieldInfo? _field;
    private readonly Func<object?>? _target;

    public ReflectionPropertyRef(PropertyInfo prop, Func<object?>? target) { _prop = prop; _target = target; Id = prop.Name; }
    public ReflectionPropertyRef(FieldInfo field, Func<object?>? target) { _field = field; _target = target; Id = field.Name; }

    public override string Id { get; }
    public MemberInfo Member => (MemberInfo?)_prop ?? _field!;
    public override Type? ValueType => _prop?.PropertyType ?? _field?.FieldType;
    public override bool CanWrite => _prop != null ? _prop.GetSetMethod() != null : _field != null && !_field.IsInitOnly && !_field.IsLiteral;
    public override object? Get()
    {
        var t = _target?.Invoke();
        return _prop != null ? _prop.GetValue(t) : _field!.GetValue(t);
    }
    public override void Set(object value)
    {
        var t = _target?.Invoke();
        if (_prop != null) _prop.SetValue(t, value); else _field!.SetValue(t, value);
    }
}
