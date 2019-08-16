using Unity.Entities;

[InternalBufferCapacity(0)]
public struct InvContent : IBufferElementData
{
    public float RecordedPrice;
    public float Quantity;

    public InvContent(float quantity, float recordedPrice)
    {
        RecordedPrice = recordedPrice;
        Quantity = quantity;
    }

    public override string ToString()
    {
        return $"Quantity: {Quantity}. Recorded Price: {RecordedPrice}.";
    }
}