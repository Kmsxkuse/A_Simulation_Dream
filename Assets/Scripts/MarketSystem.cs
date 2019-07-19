using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// ReSharper disable ForCanBeConvertedToForeach

public class MarketSystem : JobComponentSystem
{
    private struct Offer
    {
        public Entity Source;
        public int Units;
        public float Cost;

        public Offer(Entity source, int units, float cost)
        {
            Source = source;
            Units = units;
            Cost = cost;
        }
    }

    private int _frameCount;

    private NativeMultiHashMap<int, Offer> _tradeAsks;
    private NativeMultiHashMap<int, Offer> _tradeBids;
    private NativeMultiHashMap<int, float2> _goodHistories;
    
    private BufferFromEntity<CostOfLiving> _costOfLiving;
    private BufferFromEntity<LimitGood> _limitGoods;
    private BufferFromEntity<PossibleDelta> _possibleDeltas;
    private BufferFromEntity<DeltaValue> _deltaValues;
    private BufferFromEntity<IdealQuantity> _idealQuantity;
    private ComponentDataFromEntity<Inventory> _inventorySizes;

    private NativeArray<Entity> _goodEntities;
    private NativeArray<Good> _goods;
    private NativeArray<float3> _goodsData;
    private NativeQueue<float3> _forgottenGoodsData;

    private EntityCommandBuffer _ecb;

    [BurstCompile]
    private struct ProcessAgentLogic : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<CostOfLiving> costOfLiving;
        [ReadOnly] public BufferFromEntity<LimitGood> limitGoods;
        [ReadOnly] public BufferFromEntity<PossibleDelta> possibleDeltas;
        [ReadOnly] public BufferFromEntity<DeltaValue> deltaValues;

        [ReadOnly] public Unity.Mathematics.Random randomNumber;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;
        
        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Agent agent)
        {
            var targetLivingCosts = costOfLiving[agent.Logic].AsNativeArray();
            var targetInventory = inventoryContents[entity].AsNativeArray();
            
            // Checking for minimum living cost expenditure. Typically just 1 unit of food.
            for (var i = 0; i < targetLivingCosts.Length; i++)
            {
                var currentLivingCost = targetLivingCosts[i];
                var placeholder = targetInventory[currentLivingCost.Good];
                placeholder.Quantity -= currentLivingCost.Quantity;

                if (placeholder.Quantity < 0)
                {
                    agent.Starving = true;
                    placeholder.Quantity = 0;
                }
                
                targetInventory[currentLivingCost.Good] = placeholder;
            }

            // Agents without minimum living cost skip straight to market.
            if (agent.Starving)
                return;

            var targetLimitGoods = limitGoods[agent.Logic].AsNativeArray();
            
            // Determining if Agent has too many produced goods.
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var i = 0; i < targetLimitGoods.Length; i++)
            {
                var currentLimitGood = targetLimitGoods[i];
                if (targetInventory[currentLimitGood.Good].Quantity >= currentLimitGood.Quantity)
                    return;
            }

            var targetPossibleDeltas = possibleDeltas[agent.Logic].AsNativeArray();
            var targetDeltaValues = deltaValues[agent.Logic].AsNativeArray();
            
            var selectedDelta = new int3(-1);

            // Calculating proper delta/production line given current inventory
            for (var i = 0; i < targetPossibleDeltas.Length; i++)
            {
                var range = targetPossibleDeltas[i].Deltas;
                var checker = true;
                
                for (var j = range.x; j < range.y; j++)
                    checker = checker &&
                              targetInventory[targetDeltaValues[j].Good].Quantity 
                              >= (targetDeltaValues[j].Quantity > 0 ? targetDeltaValues[j].Quantity : 1);
                
                if (!checker)
                    continue;

                selectedDelta = targetPossibleDeltas[i].Deltas;
                break;
            }

            if (selectedDelta.x < 0)
                return;
            
            var variableConsumption = 1f;

            // First pass of consumption, or input, of goods to find lowest variable consumption.
            for (var consume = selectedDelta.x; consume < selectedDelta.y; consume++)
            {
                var currentDeltaValue = targetDeltaValues[consume];

                // Quantities in the negative number represent "can produce up to value".
                // Produces either maximum allowed by current inventory or abs(neg value).
                if (currentDeltaValue.Quantity > 0)
                    continue;

                // Consumption is determined by lowest ratio of current inventory to maximum consumed.
                var percentConsumed = Mathf.Clamp01((float) targetInventory[currentDeltaValue.Good].Quantity 
                                                    / Mathf.Abs(currentDeltaValue.Quantity));

                variableConsumption = percentConsumed < variableConsumption
                    ? percentConsumed
                    : variableConsumption;
            }
            
            // Second pass of consumption
            for (var consume = selectedDelta.x; consume < selectedDelta.y; consume++)
            {
                var currentDeltaValue = targetDeltaValues[consume];
                var placeholder = targetInventory[currentDeltaValue.Good];
                
                // Must roll lower than probability to consume good
                if (randomNumber.NextFloat(0, 1) > currentDeltaValue.Possibility)
                    continue;

                placeholder.Quantity -= Mathf.FloorToInt(Mathf.Abs(currentDeltaValue.Quantity) * variableConsumption);
                targetInventory[currentDeltaValue.Good] = placeholder;
            }

            for (var produce = selectedDelta.y; produce < selectedDelta.z; produce++)
            {
                var currentDeltaValue = targetDeltaValues[produce];
                var placeholder = targetInventory[currentDeltaValue.Good];
                // Quantities less than zero for production multiplies consumption by value.
                placeholder.Quantity += currentDeltaValue.Quantity > 0 
                    ? currentDeltaValue.Quantity
                    : Mathf.FloorToInt(Mathf.Abs(currentDeltaValue.Quantity) * variableConsumption);
                targetInventory[currentDeltaValue.Good] = placeholder;
            }
        }
    }

    //[BurstCompile]
    private struct CalculateHistories : IJob
    {
        [ReadOnly] public int goodsCount, currentFrameCount;
        
        [WriteOnly] public NativeQueue<float3> forgotten;
        [WriteOnly] public NativeArray<float3> goodsData;
        
        public NativeMultiHashMap<int, float2> history;
        
        public void Execute()
        {
            for (var index = 0; index < goodsCount; index++)
            {
                var average = 0f;
                var transactions = 0;
                var minimum = Mathf.Infinity;
                var maximum = Mathf.NegativeInfinity;
                
                if (!history.TryGetFirstValue(index, out var meanData, out var iterator))
                    continue;

                do
                {
                    if (currentFrameCount - meanData.y > 10) // how many turns to look back
                    {
                        forgotten.Enqueue(new float3(index, meanData));
                        // This is why I cant parallel this.
                        history.Remove(iterator);
                        continue;
                    }
                    
                    average += (meanData.x - average) / ++transactions;

                    if (minimum > meanData.x)
                        minimum = meanData.x;
                    if (maximum < meanData.x)
                        maximum = meanData.x;
                    
                } while (history.TryGetNextValue(out meanData, ref iterator));
                
                goodsData[index] = new float3(average, minimum, maximum);
            }
        }
    }
    
    // ECB kills Burst.
    private struct AssignGoodData : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> goodsData;
        [ReadOnly] public NativeArray<Entity> goodEntities;

        [WriteOnly] public EntityCommandBuffer.Concurrent ecb;
        
        [NativeDisableParallelForRestriction] public NativeArray<Good> goods;
        
        public void Execute(int index)
        {
            var placeholder = goods[index];
            placeholder.Mean = goodsData[index].x;
            placeholder.Minimum = goodsData[index].y;
            placeholder.Maximum = goodsData[index].z;
            goods[index] = placeholder;
            
            ecb.SetComponent(index, goodEntities[index], placeholder);
        }
    }

    [BurstCompile]
    private struct GenerateOffers : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<IdealQuantity> idealQuantities;
        [ReadOnly] public BufferFromEntity<InvContent> inventoryContents;
        [ReadOnly] public ComponentDataFromEntity<Inventory> inventorySizes;
        [ReadOnly] public NativeArray<Good> goods;

        [WriteOnly] public NativeMultiHashMap<int, Offer>.Concurrent tradeAsks;
        [WriteOnly] public NativeMultiHashMap<int, Offer>.Concurrent tradeBids;
        
        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent agent)
        {
            // Determining if surplus or shortage
            var targetInventory = inventoryContents[entity].AsNativeArray();
            var targetIdeals = idealQuantities[agent.Logic].AsNativeArray();

            for (var good = 0; good < targetInventory.Length; good++)
            {
                var targetInvContent = targetInventory[good];
                if (targetInvContent.Quantity > targetIdeals[good])
                {
                    // Surplus
                    var reasonablePrice = targetInvContent.OriginalCost * 1.02f;
                    //agent.NumProduct = targetGood.Quantity; // No clue what this does
                    // Intentional placing entire inventory out to market.
                    tradeAsks.Add(good, new Offer(entity, targetInvContent.Quantity, reasonablePrice));
                }
                else
                {
                    // Shortage
                    var invSpaceLeft = inventorySizes[entity].MaxSize - inventorySizes[entity].Occupied;
                    var goodSize = goods[good].SpaceOccupied;
                    if (goodSize > invSpaceLeft)
                        continue; // No space left!

                    var shortage = targetIdeals[good] - targetInvContent.Quantity;
                    var limit = shortage * goodSize <= invSpaceLeft
                        ? shortage
                        : Mathf.FloorToInt((float) invSpaceLeft / goodSize);

                    var bidPrice = 0; // Why not get it for free?
                }
            }
        }
    }

    protected override JobHandle OnUpdate(JobHandle inputDependencies)
    {
        Debug.Break();
        
        _frameCount = Time.frameCount;
        _forgottenGoodsData.Clear();
        
        var calculateGoods = new CalculateHistories
        {
            currentFrameCount = _frameCount,
            goodsCount = _goodEntities.Length,
            forgotten = _forgottenGoodsData,
            goodsData = _goodsData,
            history = _goodHistories
        }.Schedule(inputDependencies);
        
        calculateGoods = new AssignGoodData
        {
            goodsData = _goodsData,
            goodEntities = _goodEntities,
            goods = _goods,
            ecb = _ecb.ToConcurrent()
        }.Schedule(_goodEntities.Length, 1, calculateGoods);
        
        var marketJobs = new ProcessAgentLogic
        {
            costOfLiving = _costOfLiving,
            limitGoods = _limitGoods,
            possibleDeltas = _possibleDeltas,
            deltaValues = _deltaValues,
            randomNumber = new Unity.Mathematics.Random((uint) UnityEngine.Random.Range(1, 100000)),
            inventoryContents = GetBufferFromEntity<InvContent>()
        }.Schedule(this, inputDependencies);
        
        marketJobs = new GenerateOffers
        {
            idealQuantities = _idealQuantity,
            inventoryContents = GetBufferFromEntity<InvContent>(true),
            inventorySizes = _inventorySizes,
            goods = _goods,
            tradeAsks = _tradeAsks.ToConcurrent(),
            tradeBids = _tradeBids.ToConcurrent()
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, calculateGoods));
        
        return marketJobs;
    }

    protected override void OnCreate()
    {
        _ecb = new EntityCommandBuffer(Allocator.Persistent);
        
        _costOfLiving = GetBufferFromEntity<CostOfLiving>(true);
        _limitGoods = GetBufferFromEntity<LimitGood>(true);
        _possibleDeltas = GetBufferFromEntity<PossibleDelta>(true);
        _deltaValues = GetBufferFromEntity<DeltaValue>(true);
        _idealQuantity = GetBufferFromEntity<IdealQuantity>(true);
        _inventorySizes = GetComponentDataFromEntity<Inventory>(true);
        
        _goodHistories = new NativeMultiHashMap<int, float2>(1000, Allocator.Persistent);

        _goodEntities = GetEntityQuery(typeof(Good)).ToEntityArray(Allocator.Persistent);
        _goods = new NativeArray<Good>(_goodEntities.Length, Allocator.Persistent);
        for (var index = 0; index < _goodEntities.Length; index++)
        {
            _goods[index] = EntityManager.GetComponentData<Good>(_goodEntities[index]);
            _goodHistories.Add(index, new float2(_goods[index].Mean, 0)); // Push 2 fake trades to generate starting range.
            _goodHistories.Add(index, new float2(_goods[index].Mean * 3, 0));
        }
        _goodsData = new NativeArray<float3>(_goodEntities.Length, Allocator.Persistent);
        _forgottenGoodsData = new NativeQueue<float3>(Allocator.Persistent);
        
        _tradeAsks = new NativeMultiHashMap<int, Offer>(1000, Allocator.Persistent);
        _tradeBids = new NativeMultiHashMap<int, Offer>(1000, Allocator.Persistent);
    }

    protected override void OnDestroy()
    {
        _ecb.Dispose();
        _tradeAsks.Dispose();
        _tradeBids.Dispose();
        _goods.Dispose();
        _goodEntities.Dispose();
        _goodsData.Dispose();
        _goodHistories.Dispose();
        _forgottenGoodsData.Dispose();
    }
}