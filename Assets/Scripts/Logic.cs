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
public struct CostOfLiving : IBufferElementData
{
    public readonly int Good;
    public readonly int Quantity;

    public CostOfLiving(int good, int quantity)
    {
        Good = good;
        Quantity = quantity;
    }
}

[InternalBufferCapacity(0)]
public struct LimitGood : IBufferElementData
{
    public readonly int Good;
    public readonly int Quantity;

    public LimitGood(int good, int quantity)
    {
        Good = good;
        Quantity = quantity;
    }
}

[InternalBufferCapacity(0)]
public struct IdealQuantity : IBufferElementData
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
}

[InternalBufferCapacity(0)]
public struct PossibleDelta : IBufferElementData
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
}

[InternalBufferCapacity(0)]
public struct DeltaValue : IBufferElementData
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
public struct JsonLPossibilities
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
}

[Serializable]
public struct JsonLValues
{
    public string name;
    public int quantity;

    public JsonLValues(string name, int quantity)
    {
        this.name = name;
        this.quantity = quantity;
    }
}