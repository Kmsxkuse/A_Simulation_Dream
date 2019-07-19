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
    public float RecordedPrice;
    public int Quantity;

    public InvContent(int quantity, float recordedPrice = 0)
    {
        RecordedPrice = recordedPrice;
        Quantity = quantity;
    }

    public override string ToString()
    {
        return $"Quantity: {Quantity}. Original Cost: {RecordedPrice}.";
    }
}