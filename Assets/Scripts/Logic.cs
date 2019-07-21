using System;
using System.Collections.Generic;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct Logic : IComponentData
{
    public readonly int Index;

    public Logic(int index)
    {
        Index = index;
    }
}

[InternalBufferCapacity(0)]
public struct CostOfLivingAndLimitGood : IBufferElementData, IEquatable<CostOfLivingAndLimitGood>
{
    public int CostOfLiving;
    public int LimitGoods;

    public CostOfLivingAndLimitGood(int costOfLiving, int limitGoods = 999)
    {
        CostOfLiving = costOfLiving;
        LimitGoods = limitGoods;
    }

    public bool Equals(CostOfLivingAndLimitGood other)
    {
        return CostOfLiving == other.CostOfLiving && LimitGoods == other.LimitGoods;
    }

    public override bool Equals(object obj)
    {
        return obj is CostOfLivingAndLimitGood other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (CostOfLiving * 397) ^ LimitGoods;
        }
    }

    public static bool operator ==(CostOfLivingAndLimitGood left, CostOfLivingAndLimitGood right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CostOfLivingAndLimitGood left, CostOfLivingAndLimitGood right)
    {
        return !left.Equals(right);
    }
}

[InternalBufferCapacity(0)]
public struct IdealQuantity : IBufferElementData, IEquatable<IdealQuantity>
{
    private readonly int _quantity;

    private IdealQuantity(int quantity)
    {
        _quantity = quantity;
    }

    public static implicit operator IdealQuantity(int i)
    {
        return new IdealQuantity(i);
    }

    public static implicit operator int(IdealQuantity iq)
    {
        return iq._quantity;
    }

    public bool Equals(IdealQuantity other)
    {
        return _quantity == other._quantity;
    }

    public override bool Equals(object obj)
    {
        return obj is IdealQuantity other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _quantity;
    }

    public static bool operator ==(IdealQuantity left, IdealQuantity right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(IdealQuantity left, IdealQuantity right)
    {
        return !left.Equals(right);
    }
}

[InternalBufferCapacity(0)]
public struct PossibleDelta : IBufferElementData, IEquatable<PossibleDelta>
{
    public readonly int3 Deltas; // x: Consume start. y: Produce start. z: Produce end.

    private PossibleDelta(int3 deltas)
    {
        Deltas = deltas;
    }

    public static implicit operator PossibleDelta(int3 i)
    {
        return new PossibleDelta(i);
    }

    public bool Equals(PossibleDelta other)
    {
        return Deltas.Equals(other.Deltas);
    }

    public override bool Equals(object obj)
    {
        return obj is PossibleDelta other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Deltas.GetHashCode();
    }

    public static bool operator ==(PossibleDelta left, PossibleDelta right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PossibleDelta left, PossibleDelta right)
    {
        return !left.Equals(right);
    }
}

[InternalBufferCapacity(0)]
public struct DeltaValue : IBufferElementData, IEquatable<DeltaValue>
{
    public readonly int Good;
    public int Quantity;
    public float Possibility;

    public DeltaValue(int good, int quantity, float possibility)
    {
        Good = good;
        Quantity = quantity;
        Possibility = possibility;
    }

    public override string ToString()
    {
        return $"Good: {Good}. Quantity: {Quantity}. Possibility: {Possibility}.";
    }

    public bool Equals(DeltaValue other)
    {
        return Good == other.Good;
    }

    public override bool Equals(object obj)
    {
        return obj is DeltaValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Good;
    }

    public static bool operator ==(DeltaValue left, DeltaValue right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DeltaValue left, DeltaValue right)
    {
        return !left.Equals(right);
    }
}

[Serializable]
public struct JsonLogic
{
    public string name;
    public List<JsonLValues> costOfLiving; // goods consumed in order to function regardless of limit goods
    public List<JsonLValues> limitGoods; // if agent has more than or equal these goods in inventory, stop working.
    public List<JsonLValues> idealQuantity; // agent will buy or sell to these values
    public List<JsonLValues> startQuantity; // quantity of goods in inventory at start

    public List<JsonDeltas>
        possibleDeltas; // all possible input and outputs in queue. First valid requirement gets produced.

    public static JsonLogic CreateFromJson(string path)
    {
        return JsonUtility.FromJson<JsonLogic>(File.ReadAllText(path));
    }
}

[Serializable]
public struct JsonDeltas
{
    public List<JsonLPossibilities> consumes; // inputs
    public List<JsonLPossibilities> produces; // outputs
}

[Serializable]
public struct JsonLPossibilities : IEquatable<JsonLPossibilities>
{
    public string name;
    public int quantity;
    public float possibility; // Possibility to produce or consume. 0.1 = 10%

    public JsonLPossibilities(string name, int quantity, float possibility)
    {
        this.name = name;
        this.quantity = quantity;
        this.possibility = possibility;
    }

    public bool Equals(JsonLPossibilities other)
    {
        return string.Equals(name, other.name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object obj)
    {
        return obj is JsonLPossibilities other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(name);
    }

    public static bool operator ==(JsonLPossibilities left, JsonLPossibilities right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(JsonLPossibilities left, JsonLPossibilities right)
    {
        return !left.Equals(right);
    }
}

[Serializable]
public struct JsonLValues : IEquatable<JsonLValues>
{
    public string name;
    public int quantity;

    public JsonLValues(string name, int quantity)
    {
        this.name = name;
        this.quantity = quantity;
    }

    public bool Equals(JsonLValues other)
    {
        return string.Equals(name, other.name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object obj)
    {
        return obj is JsonLValues other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(name);
    }

    public static bool operator ==(JsonLValues left, JsonLValues right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(JsonLValues left, JsonLValues right)
    {
        return !left.Equals(right);
    }
}