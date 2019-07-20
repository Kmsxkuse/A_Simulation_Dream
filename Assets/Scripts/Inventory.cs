using Unity.Entities;

[InternalBufferCapacity(0)]
public struct InvContent : IBufferElementData
{
    public readonly int Size;
    public float RecordedPrice;
    public int Quantity;

    public InvContent(int quantity, int size, float recordedPrice)
    {
        RecordedPrice = recordedPrice;
        Quantity = quantity;
        Size = size;
    }

    public override string ToString()
    {
        return $"Quantity: {Quantity}. Recorded Price: {RecordedPrice}.";
    }
}

[InternalBufferCapacity(0)]
public struct InvStats : IBufferElementData
{
    public int Transactions;

    public float Mean, Minimum, Maximum;

    public InvStats(float mean)
    {
        Mean = mean;
        Minimum = mean;
        Maximum = mean * 3;
        Transactions = 0;
    }
}