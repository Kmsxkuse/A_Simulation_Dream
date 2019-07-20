using Unity.Entities;

public struct Agent : IComponentData
{
    public readonly int Index;
    public readonly Entity Logic;

    //public int NumProduct; // on market

    // Starving = true when unable to meet cost of living.
    public bool Starving;

    public Agent(int index, Entity logic) : this()
    {
        Index = index;
        Logic = logic;
    }
}

public struct Wallet : IComponentData
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
}