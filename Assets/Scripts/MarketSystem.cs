using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

// ReSharper disable ForCanBeConvertedToForeach

public class MarketSystem : JobComponentSystem
{
    private BufferFromEntity<CostOfLiving> _costOfLiving;
    private NativeMultiHashMap<Entity, float> _deltaMoney;
    private BufferFromEntity<DeltaValue> _deltaValues;

    private EntityCommandBuffer _ecb;
    private NativeQueue<float3> _forgottenGoodsData;

    private int _frameCount;

    private NativeArray<Entity> _goodEntities;
    private NativeArray<Good> _goods;
    private NativeArray<float3> _goodsData;
    private BufferFromEntity<IdealQuantity> _idealQuantity;
    private ComponentDataFromEntity<Inventory> _inventorySizes;
    private BufferFromEntity<LimitGood> _limitGoods;

    private NativeMultiHashMap<Entity, float3>
        _observedGoodHistories, _revisionism; // x: good index. y: quantity. z: frame recorded

    private BufferFromEntity<PossibleDelta> _possibleDeltas;
    private NativeMultiHashMap<int, Offer> _tradeAsks, _tradeBids;

    protected override JobHandle OnUpdate(JobHandle inputDependencies)
    {
        Debug.Break();

        _frameCount = Time.frameCount;

        var calculateGoods = new CleanStarting
        {
            revisionism = _revisionism,
            forgotten = _forgottenGoodsData
        }.Schedule(inputDependencies);

        calculateGoods = new CalculateHistories
        {
            currentFrameCount = _frameCount,
            forgotten = _forgottenGoodsData.ToConcurrent(),
            goodsData = _goodsData,
            history = _observedGoodHistories,
            revisionist = _revisionism.ToConcurrent()
        }.Schedule(this, calculateGoods);

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
            randomNumber = new Random((uint) UnityEngine.Random.Range(1, 100000)),
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

        marketJobs = new ResolveOffers
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            currentFrame = _frameCount,
            inventoryContents = GetBufferFromEntity<InvContent>(),
            goodHistory = _revisionism.ToConcurrent(),
            deltaMoney = _deltaMoney.ToConcurrent()
        }.Schedule(_goods.Length, 1, marketJobs);

        var completeChanges = new ProcessMoneyChanges
        {
            deltaMoney = _deltaMoney
        }.Schedule(this, marketJobs);

        completeChanges = new RewriteHistory
        {
            history = _observedGoodHistories.ToConcurrent(),
            revisionism = _revisionism
        }.Schedule(this, completeChanges);

        marketJobs = new ResetMultiHashMaps
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            deltaMoney = _deltaMoney,
            history = _observedGoodHistories
        }.Schedule(JobHandle.CombineDependencies(marketJobs, completeChanges));

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

        _observedGoodHistories = new NativeMultiHashMap<Entity, float3>(1000, Allocator.Persistent);
        _revisionism = new NativeMultiHashMap<Entity, float3>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _tradeAsks = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _tradeBids = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _deltaMoney = new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity, Allocator.Persistent);

        _goodEntities = GetEntityQuery(typeof(Good)).ToEntityArray(Allocator.Persistent);
        _goods = new NativeArray<Good>(_goodEntities.Length, Allocator.Persistent);
        for (var index = 0; index < _goodEntities.Length; index++)
        {
            _goods[index] = EntityManager.GetComponentData<Good>(_goodEntities[index]);

            using (var agents = GetEntityQuery(typeof(Agent)).ToEntityArray(Allocator.TempJob))
            {
                foreach (var agentEntity in agents)
                {
                    // Pushing 2 fake trades to establish observed range
                    _observedGoodHistories.Add(agentEntity, new float3(index, _goods[index].Mean, 0));
                    _observedGoodHistories.Add(agentEntity, new float3(index, _goods[index].Mean * 3, 0));
                }
            }
        }

        _goodsData = new NativeArray<float3>(_goodEntities.Length, Allocator.Persistent);
        _forgottenGoodsData = new NativeQueue<float3>(Allocator.Persistent);
    }

    protected override void OnStopRunning()
    {
        _ecb.Dispose();
        _goods.Dispose();
        _goodEntities.Dispose();
        _goodsData.Dispose();
        _observedGoodHistories.Dispose();
        _forgottenGoodsData.Dispose();
        _tradeAsks.Dispose();
        _tradeBids.Dispose();
        _deltaMoney.Dispose();
    }

    private struct Offer
    {
        public readonly Entity Source;
        public int Units;
        public readonly float Cost;

        public Offer(Entity source, int units, float cost)
        {
            Source = source;
            Units = units;
            Cost = cost;
        }
    }

    [BurstCompile]
    private struct ProcessAgentLogic : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<CostOfLiving> costOfLiving;
        [ReadOnly] public BufferFromEntity<LimitGood> limitGoods;
        [ReadOnly] public BufferFromEntity<PossibleDelta> possibleDeltas;
        [ReadOnly] public BufferFromEntity<DeltaValue> deltaValues;

        [ReadOnly] public Random randomNumber;

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

    [BurstCompile]
    private struct CalculateHistories : IJobForEachWithEntity<Inventory>
    {
        [ReadOnly] public int currentFrameCount;

        [WriteOnly] public NativeQueue<float3>.Concurrent forgotten;
        [WriteOnly] public NativeArray<float3> goodsData;

        [ReadOnly] public NativeMultiHashMap<Entity, float3> history;
        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent revisionist;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int jobIndex, ref Inventory throwaway)
        {
            if (!history.TryGetFirstValue(entity, out var meanData, out var iterator))
                return;

            var average = 0f;
            var transactions = 0;
            var minimum = Mathf.Infinity;
            var maximum = Mathf.NegativeInfinity;

            do
            {
                if (currentFrameCount - meanData.z > 10) // how many turns to look back
                {
                    forgotten.Enqueue(meanData);
                    continue;
                }

                average += (meanData.y - average) / ++transactions;

                minimum = Mathf.Min(minimum, meanData.y);
                maximum = Mathf.Max(maximum, meanData.y);

                revisionist.Add(entity, meanData);
            } while (history.TryGetNextValue(out meanData, ref iterator));

            goodsData[Mathf.RoundToInt(meanData.x)] = new float3(average, minimum, maximum);
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

        [WriteOnly] public NativeMultiHashMap<int, Offer>.Concurrent tradeAsks, tradeBids;

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
                    var reasonablePrice = targetInvContent.RecordedPrice * 1.02f;
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

                    const int bidPrice = 0; // Why not get it for free?

                    // Higher if closer to minimum;
                    var preference = 1 - Mathf.Clamp01((goods[good].Mean - goods[good].Minimum) /
                                                       (goods[good].Maximum - goods[good].Minimum));

                    var purchaseQuantity = Mathf.Clamp(Mathf.RoundToInt(preference * shortage),
                        1, limit);

                    tradeBids.Add(good, new Offer(entity, purchaseQuantity, bidPrice));
                }
            }
        }
    }

    [BurstCompile]
    private struct ResolveOffers : IJobParallelFor
    {
        [ReadOnly] public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        [ReadOnly] public int currentFrame;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent goodHistory;
        [WriteOnly] public NativeMultiHashMap<Entity, float>.Concurrent deltaMoney;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute(int index)
        {
            var currentAsks = new NativeList<Offer>(Allocator.Temp);
            var currentBids = new NativeList<Offer>(Allocator.Temp);

            var numAsks = 0;
            var numBids = 0;

            if (!tradeAsks.TryGetFirstValue(index, out var currentOffer, out var iterator))
                return;

            do
            {
                currentAsks.Add(currentOffer);
                numAsks += currentOffer.Units;
            } while (tradeAsks.TryGetNextValue(out currentOffer, ref iterator));

            if (!tradeBids.TryGetFirstValue(index, out currentOffer, out iterator))
                return;

            do
            {
                currentBids.Add(currentOffer);
                numBids += currentOffer.Units;
            } while (tradeBids.TryGetNextValue(out currentOffer, ref iterator));

            currentAsks.AsArray().Sort(Comparer<Offer>.Create(
                (x, y) => x.Cost > y.Cost ? -1 : x.Cost < y.Cost ? 1 : 0));

            while (currentBids.Length > 0 && currentAsks.Length > 0)
            {
                var buyer = currentBids[0];
                var seller = currentAsks[0];

                var quantityTraded = Mathf.Min(buyer.Units, seller.Units);
                var clearingPrice = seller.Cost;

                if (quantityTraded > 0)
                {
                    // Transferring goods
                    seller.Units -= quantityTraded;
                    buyer.Units -= quantityTraded;

                    var targetInv = inventoryContents[seller.Source].AsNativeArray();
                    var placeholder = targetInv[index];
                    placeholder.Quantity =
                        Mathf.Clamp(placeholder.Quantity - quantityTraded, 0, placeholder.Quantity);
                    // No change to recorded price by seller.
                    targetInv[index] = placeholder;

                    targetInv = inventoryContents[buyer.Source].AsNativeArray();
                    placeholder = targetInv[index];
                    placeholder.RecordedPrice = (placeholder.Quantity * placeholder.RecordedPrice + quantityTraded
                                                 * clearingPrice) / (placeholder.Quantity + quantityTraded);
                    placeholder.Quantity += quantityTraded;

                    deltaMoney.Add(seller.Source, clearingPrice * quantityTraded);
                    deltaMoney.Add(buyer.Source, -clearingPrice * quantityTraded);
                }
            }
        }
    }

    [BurstCompile]
    private struct ProcessMoneyChanges : IJobForEachWithEntity<Wallet>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float> deltaMoney;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Wallet wallet)
        {
            if (!deltaMoney.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            do
            {
                wallet.Money += transaction;
            } while (deltaMoney.TryGetNextValue(out transaction, ref iterator));
        }
    }

    [BurstCompile]
    private struct ResetMultiHashMaps : IJob
    {
        public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        public NativeMultiHashMap<Entity, float> deltaMoney;
        public NativeMultiHashMap<Entity, float3> history;

        public void Execute()
        {
            tradeAsks.Clear();
            tradeBids.Clear();
            deltaMoney.Clear();
            history.Clear();
        }
    }

    [BurstCompile]
    private struct RewriteHistory : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float3> revisionism;
        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent history;

        public void Execute(Entity entity, int index, ref Agent throwaway)
        {
            if (!revisionism.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            do
            {
                history.Add(entity, transaction);
            } while (revisionism.TryGetNextValue(out transaction, ref iterator));
        }
    }

    [BurstCompile]
    private struct CleanStarting : IJob
    {
        public NativeMultiHashMap<Entity, float3> revisionism;
        public NativeQueue<float3> forgotten;

        public void Execute()
        {
            forgotten.Clear();
            revisionism.Clear();
        }
    }
}