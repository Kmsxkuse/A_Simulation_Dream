using Unity.Entities;

public struct Inventory : IComponentData
{
    // Maximum volume of goods allowed. Think warehouses.
    public readonly int MaxSize;
    public int Occupied;

    public Inventory(int maxSize, int occupied)
    {
        MaxSize = maxSize;
        Occupied = occupied;
    }
}

[InternalBufferCapacity(0)]
public struct InvContent : IBufferElementData
{
    // Index in Dynamic Buffer determines type.
    public readonly float OriginalCost;
    public int Quantity;

    public InvContent(int quantity, float originalCost = 0)
    {
        OriginalCost = originalCost;
        Quantity = quantity;
    }

    public override string ToString() =>
        $"Quantity: {Quantity}. Original Cost: {OriginalCost}.";
}