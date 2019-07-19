﻿using System;
using System.IO;
using Unity.Entities;
using UnityEngine;

public struct Good : IComponentData, IEquatable<Good>
{
    public readonly int Index;
    public readonly int SpaceOccupied;

    public float Mean;
    public float Minimum;
    public float Maximum;

    public Good(int index, JsonGood jsonGood)
    {
        Index = index;
        SpaceOccupied = jsonGood.size;
        Mean = jsonGood.initialCost;
        Minimum = 0;
        Maximum = Mean;
    }

    public bool Equals(Good other)
    {
        return Index == other.Index;
    }

    public override bool Equals(object obj)
    {
        return obj is Good other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Index;
    }

    public static bool operator ==(Good left, Good right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Good left, Good right)
    {
        return !left.Equals(right);
    }
}

[Serializable]
public struct JsonGood
{
    public string name;
    public int size;
    public float initialCost;

    public static JsonGood CreateFromJson(string path) => 
        JsonUtility.FromJson<JsonGood>(File.ReadAllText(path));
}