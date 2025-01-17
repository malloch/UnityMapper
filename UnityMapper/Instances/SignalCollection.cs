using Mapper;
using UnityEngine;
using UnityMapper.API;
using Time = Mapper.Time;

namespace UnityMapper.Instances;

/// <summary>
/// This class is an abstraction over a Signal that allows for manipulation of multiple instances of the same signal.
///
/// This class also dictates how individual signals can be grouped into instances
/// </summary>
public class SignalCollection
{
    private Device _device;
    private Signal _signal;
    private SignalSpec _spec;
    private readonly Dictionary<ulong, SignalSpec> _signals = [];

    /// <summary>
    /// Create a new SignalCollection with a single default instance.
    /// </summary>
    /// <param name="device">Libmapper device this collection is a part of</param>
    /// <param name="spec">The first signal instance to be created</param>
    public SignalCollection(Device device, SignalSpec spec)
    {
        _spec = spec;
        _device = device;
        Name = GetFullPathname(spec.Owner) + "/" + spec.LocalName;
        _signal = device.AddSignal(spec.Type == SignalType.ReadOnly ? Signal.Direction.Outgoing : Signal.Direction.Incoming, Name, spec.Property.GetVectorLength(),
            LibmapperDevice.CreateLibmapperTypeFromPrimitive(spec.Property.GetMappedType()), spec.Property.Units, 0);
        if (spec.Property.Bounds != null)
        {
            _signal.SetProperty(Property.Min, spec.Property.Bounds.Value.min);
            _signal.SetProperty(Property.Max, spec.Property.Bounds.Value.max);
        }

        var iid = spec.OwningList.GetInstanceID();
        var id = iid < 0 ? (ulong) Math.Abs(iid) : ((ulong) iid) + Int32.MaxValue;
        spec.AssignInstanceID(id);
        _signals.Add(id, spec);
        _signal.ReserveInstance(id);
        // Debug.Log($"Reserved {_spec.Property.GetName()} instance{id}");
        _signal.SetProperty(Property.Ephemeral, spec.Ephemeral);
    }

    /// <summary>
    /// A full path name to the signal, from the highest root GameObject.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// If this collection can accept the specified discovered signal
    /// </summary>
    /// <param name="other"></param>
    public bool CanAccept(SignalSpec other) => _spec.CanGroupWith(other);

    public void Update()
    {
        var numInstances = _signal.GetNumInstances(Signal.Status.Active);
        for (var i = 0; i < numInstances; i++)
        {
            var instance = _signal.GetInstance(i, Signal.Status.Active);
            var status = instance.GetStatus();
            if (status.HasFlag(Signal.Status.UpstreamRelease))
            {
                Console.WriteLine($"_spec.Ephemeral = {_spec.Ephemeral}");
                if (_spec.Ephemeral)
                {
                    _signal.Release(instance.id);
                    _signals[instance.id].Property.Reset();
                    continue;
                }
            }
            if (status.HasFlag(Signal.Status.RemoteUpdate))
            {
                // update local
                var value = _signal.GetValue(instance.id);
                _signals[instance.id].Property.SetObject(value.Item1);
            }
            else
            {
                // push local
                // TODO: don't do this unless the value has changed?
                var value = _signals[instance.id].Property.GetValue();
                var oldValue = _signal.GetValue(instance.id);
                if (value != oldValue.Item1)
                    _signal.SetValue(value, instance.id);
            }
        }
    }

    public void Add(SignalSpec toAdd)
    {
        if (!CanAccept(toAdd))
        {
            throw new InvalidOperationException("Cannot accept signal");
        }

        var iid = toAdd.OwningList.GetInstanceID();
        var id = iid < 0 ? (ulong) Math.Abs(iid) : ((ulong) iid) + Int32.MaxValue;
        toAdd.AssignInstanceID(id);
        _signals.Add(id, toAdd);
        _signal.ReserveInstance(id);
        // Debug.Log($"Reserved {toAdd.Property.GetName()} instance{id}");
    }

    public void RemoveAllFromList(LibmapperComponentList target)
    {
        // If any signals are owned by this component, remove them
        var toRemove = new List<ulong>();
        foreach (var id in _signals.Keys)
        {
            if (_signals[id].OwningList == target)
            {
                toRemove.Add(id);
            }
        }

        foreach (var id in toRemove)
        {
            _signal.RemoveInstance(id);
            _signals.Remove(id);
        }
    }


    private static string GetFullPathname(GameObject obj)
    {
        var path = obj.name;
        while (obj.transform.parent != null)
        {
            obj = obj.transform.parent.gameObject;
            path = obj.name + "/" + path;
        }

        return path;
    }
}

/// <summary>
/// Contains uniquely identifying information about a signal. Used by a <see cref="SignalCollection"/> to group signals.
/// </summary>
public record SignalSpec(string LocalName, GameObject Owner, IBoundProperty Property, LibmapperComponentList OwningList)
{
    /// <summary>
    /// The name of this signal, relative to it's owning object. For example, a camera's FOV slider would be "Camera/fov"
    /// </summary>
    public string LocalName { get; private set; } = LocalName;

    /// <summary>
    /// Whether this signal is ephemeral or not.
    /// </summary>
    public bool Ephemeral => OwningList.isEphemeral;

    /// <summary>
    /// Whether this signal can be instanced or not.
    /// If false, CanGroupWith(SignalSpec) always returns false.
    /// </summary>
    public bool CanInstance => OwningList.canInstance;

    /// <summary>
    /// Read/write mode of this signal
    /// </summary>
    public SignalType Type => OwningList.type;

    /// <summary>
    /// The GameObject that this property belongs to.
    /// </summary>
    public GameObject Owner { get; private set; } = Owner;

    /// <summary>
    /// The LibmapperComponentList used to discover this signal
    /// </summary>
    public LibmapperComponentList OwningList { get; private set; } = OwningList;

    /// <summary>
    /// Accessor for the property on that specific object
    /// </summary>
    public IBoundProperty Property { get; private set; } = Property;

    /// <summary>
    /// Internal instance ID of this signal. Can only be set once.
    /// </summary>
    public ulong InstanceId { get; private set; } = 0;

    private bool _hasBeenAssigned = false;
    public void AssignInstanceID(ulong id)
    {
        if (_hasBeenAssigned)
        {
            throw new InvalidOperationException("Instance ID already assigned");
        }

        InstanceId = id;
        _hasBeenAssigned = true;
    }

    /// <summary>
    /// Whether this signal should be expressed as an instance of another signal
    /// </summary>
    /// <param name="other">Another signal to test for similarity</param>
    public bool CanGroupWith(SignalSpec other)
    {
        if (!CanInstance) {
            return false;
        }

        // Debug.Log($"comparing gameobjects {Owner.transform.parent.gameObject} : {other.Owner.transform.parent.gameObject}");
        // Debug.Log($"comparing local names {LocalName} : {other.LocalName}");
        // Debug.Log($"comparing owner names {Owner.name} : {other.Owner.name}");

        return Owner.transform.parent.gameObject == other.Owner.transform.parent.gameObject // both owned by the same parent
               && LocalName == other.LocalName // both have the same local name
               && SimilarName(Owner.name, other.Owner.name); // both gameobjects have the same or similar names
    }

    /// <summary>
    /// Used to determine if the names of two GameObjects are similar enough to be grouped together.
    /// Tests for name equality, and chops off the last segment of the name if it contains a period.
    /// </summary>
    /// <returns></returns>
    private static bool SimilarName(string a, string b)
    {
        if (!a.Contains("."))
        {
            return a == b;
        }

        // very slow way of doing this, should improve
        var aSplit = string.Join('.', a.Split(".")[..^1]);
        var bSplit = string.Join('.', b.Split(".")[..^1]);
        return aSplit == bSplit;
    }
}