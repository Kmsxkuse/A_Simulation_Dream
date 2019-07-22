using System;
using Unity.Entities;

public struct AgTag : IComponentData
{
    
}

public struct Agent : IComponentData
{
    public readonly Entity Logic;
    public bool Skipping;
    public float AverageRequirement;

    public Agent(Entity logic)
    {
        Logic = logic;
        Skipping = false;
        AverageRequirement = 0;
    }
}

public struct Wallet : IComponentData, IEquatable<Wallet>
{
    public float Money; // Money on hand
    public float MoneyLastRound;
    public float Profit;

    public Wallet(float money)
    {
        Money = money;
        MoneyLastRound = money;
        Profit = 0;
    }

    public bool Equals(Wallet other)
    {
        return Money.Equals(other.Money) && MoneyLastRound.Equals(other.MoneyLastRound) && Profit.Equals(other.Profit);
    }

    public override bool Equals(object obj)
    {
        return obj is Wallet other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = Money.GetHashCode();
            hashCode = (hashCode * 397) ^ MoneyLastRound.GetHashCode();
            hashCode = (hashCode * 397) ^ Profit.GetHashCode();
            return hashCode;
        }
    }

    public static bool operator ==(Wallet left, Wallet right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Wallet left, Wallet right)
    {
        return !left.Equals(right);
    }
}