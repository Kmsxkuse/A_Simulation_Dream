using System;
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

    private int _frameCount, _goodsCount;

    private BufferFromEntity<IdealQuantity> _idealQuantity;
    private BufferFromEntity<LimitGood> _limitGoods;

    private NativeMultiHashMap<Entity, float3>
        _observedGoodHistories, _revisionism; // x: good index. y: cost. z: frame recorded

    private BufferFromEntity<PossibleDelta> _possibleDeltas;
    private NativeMultiHashMap<int, Offer> _tradeAsks, _tradeBids;

    private EntityCommandBuffer _walletEcb;

    protected override JobHandle OnUpdate(JobHandle inputDependencies)
    {
        _frameCount = Time.frameCount;

        Debug.Log(_frameCount);
        Debug.Log($"History: {_observedGoodHistories.Length}. Revision: {_revisionism.Length}.");

        if (_frameCount > 50)
            throw new Exception("TEST!");

        var targetInvContent = GetBufferFromEntity<InvContent>();
        var targetInvStats = GetBufferFromEntity<InvStats>();
        var currentRng = new Random((uint) UnityEngine.Random.Range(1, 100000));

        var calcHistoryJob = new CalculateHistories
        {
            goodsCount = _goodsCount,
            history = _observedGoodHistories,
            revisionist = _revisionism.ToConcurrent(),
            inventoryStatistics = targetInvStats,
            currentFrameCount = _frameCount
        }.Schedule(this, inputDependencies);

        var oldHistoryJob = new BurnOldHistory
        {
            oldHistory = _observedGoodHistories
        }.Schedule(calcHistoryJob);

        var marketJobs = new ProcessAgentLogic
        {
            costOfLiving = _costOfLiving,
            limitGoods = _limitGoods,
            possibleDeltas = _possibleDeltas,
            deltaValues = _deltaValues,
            rng = currentRng,
            inventoryContents = targetInvContent
        }.Schedule(this, inputDependencies);

        marketJobs = new GenerateOffers
        {
            idealQuantities = _idealQuantity,
            inventoryContents = targetInvContent,
            inventoryStatistics = targetInvStats,
            tradeAsks = _tradeAsks.ToConcurrent(),
            tradeBids = _tradeBids.ToConcurrent()
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, calcHistoryJob));

        marketJobs = new ResolveOffers
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            currentFrame = _frameCount,
            inventoryContents = targetInvContent,
            rng = currentRng,
            goodHistory = _revisionism.ToConcurrent(),
            deltaMoney = _deltaMoney.ToConcurrent()
        }.Schedule(_goodsCount, 1, marketJobs);

        var completeChanges = new RewriteHistory
        {
            history = _observedGoodHistories.ToConcurrent(),
            revisionism = _revisionism
        }.Schedule(this, JobHandle.CombineDependencies(marketJobs, oldHistoryJob));

        marketJobs = new ProcessMoneyChanges
        {
            deltaMoney = _deltaMoney,
            ecb = _walletEcb.ToConcurrent()
        }.Schedule(this, marketJobs);

        marketJobs = new ResetMultiHashMaps
        {
            tradeAsks = _tradeAsks,
            tradeBids = _tradeBids,
            deltaMoney = _deltaMoney,
            revisionism = _revisionism
        }.Schedule(JobHandle.CombineDependencies(marketJobs, completeChanges));

        return marketJobs;
    }

    protected override void OnCreate()
    {
        _walletEcb = new EntityCommandBuffer(Allocator.Persistent);

        _costOfLiving = GetBufferFromEntity<CostOfLiving>(true);
        _limitGoods = GetBufferFromEntity<LimitGood>(true);
        _possibleDeltas = GetBufferFromEntity<PossibleDelta>(true);
        _deltaValues = GetBufferFromEntity<DeltaValue>(true);
        _idealQuantity = GetBufferFromEntity<IdealQuantity>(true);

        _observedGoodHistories = new NativeMultiHashMap<Entity, float3>(1000, Allocator.Persistent);
        _revisionism = new NativeMultiHashMap<Entity, float3>(_observedGoodHistories.Capacity, Allocator.Persistent);
        _tradeAsks = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _tradeBids = new NativeMultiHashMap<int, Offer>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);
        _deltaMoney = new NativeMultiHashMap<Entity, float>(_observedGoodHistories.Capacity / 4, Allocator.Persistent);

        _goodsCount = History.GoodsCount;
    }

    protected override void OnStopRunning()
    {
        _walletEcb.Dispose();
        _observedGoodHistories.Dispose();
        _tradeAsks.Dispose();
        _tradeBids.Dispose();
        _deltaMoney.Dispose();
        _revisionism.Dispose();
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

        public override string ToString()
        {
            return $"Source: {Source.Index}. Units: {Units}. Cost: {Cost}.";
        }
    }

    [BurstCompile]
    private struct ProcessAgentLogic : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<CostOfLiving> costOfLiving;
        [ReadOnly] public BufferFromEntity<LimitGood> limitGoods;
        [ReadOnly] public BufferFromEntity<PossibleDelta> possibleDeltas;
        [ReadOnly] public BufferFromEntity<DeltaValue> deltaValues;

        [ReadOnly] public Random rng;

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

            var variableConsumption = 0;

            for (var consume = selectedDelta.x; consume < selectedDelta.y; consume++)
            {
                var currentDeltaValue = targetDeltaValues[consume];
                var placeholder = targetInventory[currentDeltaValue.Good];

                // Must roll lower than probability to consume good
                if (rng.NextFloat(0, 1) > currentDeltaValue.Possibility)
                    continue;

                // Negative quantities indicate consumption up to that value.
                variableConsumption = currentDeltaValue.Quantity > 0
                    ? currentDeltaValue.Quantity
                    : placeholder.Quantity > currentDeltaValue.Quantity
                        ? currentDeltaValue.Quantity
                        : placeholder.Quantity;

                placeholder.Quantity -= variableConsumption;

                targetInventory[currentDeltaValue.Good] = placeholder;
            }

            for (var produce = selectedDelta.y; produce < selectedDelta.z; produce++)
            {
                var currentDeltaValue = targetDeltaValues[produce];
                var placeholder = targetInventory[currentDeltaValue.Good];
                // Quantities less than zero for production multiplies consumption by value.
                placeholder.Quantity += currentDeltaValue.Quantity > 0
                    ? currentDeltaValue.Quantity
                    : variableConsumption * currentDeltaValue.Quantity;
                targetInventory[currentDeltaValue.Good] = placeholder;
            }
        }
    }

    [BurstCompile]
    private struct CalculateHistories : IJobForEachWithEntity<Wallet>
    {
        [ReadOnly] public int currentFrameCount;
        [ReadOnly] public int goodsCount;
        [ReadOnly] public NativeMultiHashMap<Entity, float3> history;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent revisionist;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvStats> inventoryStatistics;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int jobIndex, [ReadOnly] ref Wallet throwaway)
        {
            if (!history.TryGetFirstValue(entity, out var meanData, out var iterator))
                return;

            var targetInvStats = inventoryStatistics[entity].AsNativeArray();

            for (var goodsIndex = 0; goodsIndex < goodsCount; goodsIndex++)
            {
                var targetGood = targetInvStats[goodsIndex];
                targetGood.Transactions = 0;
                targetInvStats[goodsIndex] = targetGood;
            }

            do
            {
                if (currentFrameCount - meanData.z > 5) // how many turns to look back
                    continue;

                var targetGood = targetInvStats[(int) meanData.x];
                targetGood.Mean += (meanData.y - targetGood.Mean) / ++targetGood.Transactions;

                targetGood.Minimum = math.min(targetGood.Minimum, meanData.y);
                targetGood.Maximum = math.max(targetGood.Maximum, meanData.y);

                revisionist.Add(entity, meanData);
                targetInvStats[(int) meanData.x] = targetGood;
            } while (history.TryGetNextValue(out meanData, ref iterator));
        }
    }

    [BurstCompile]
    private struct GenerateOffers : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public BufferFromEntity<IdealQuantity> idealQuantities;
        [ReadOnly] public BufferFromEntity<InvContent> inventoryContents;
        [ReadOnly] public BufferFromEntity<InvStats> inventoryStatistics;

        [WriteOnly] public NativeMultiHashMap<int, Offer>.Concurrent tradeAsks, tradeBids;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent agent)
        {
            // Determining if surplus or shortage
            var targetInventory = inventoryContents[entity].AsNativeArray();
            var targetStatistic = inventoryStatistics[entity].AsNativeArray();
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
                    var shortage = targetIdeals[good] - targetInvContent.Quantity;
                    const int bidPrice = 0; // Why not get it for free?

                    var targetInvStat = targetStatistic[good];
                    // Higher if closer to minimum;
                    var preference = 1 - math.clamp((targetInvStat.Mean - targetInvStat.Minimum) /
                                                    (targetInvStat.Maximum - targetInvStat.Minimum), 0, 1);

                    var purchaseQuantity = (int) math.round(preference * shortage);

                    tradeBids.Add(good, new Offer(entity, purchaseQuantity, bidPrice));
                }
            }
        }
    }

    //[BurstCompile]
    private struct ResolveOffers : IJobParallelFor
    {
        [ReadOnly] public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        [ReadOnly] public int currentFrame;
        [ReadOnly] public Random rng;

        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent goodHistory;
        [WriteOnly] public NativeMultiHashMap<Entity, float>.Concurrent deltaMoney;

        [NativeDisableParallelForRestriction] public BufferFromEntity<InvContent> inventoryContents;

        public void Execute(int index)
        {
            var currentAsks = new NativeList<Offer>(Allocator.Temp);
            var currentBids = new NativeList<Offer>(Allocator.Temp);

            //var numAsks = 0;
            //var numBids = 0;

            if (!tradeAsks.TryGetFirstValue(index, out var currentOffer, out var iterator))
                return;

            do
            {
                currentAsks.Add(currentOffer);
                //numAsks += currentOffer.Units;
            } while (tradeAsks.TryGetNextValue(out currentOffer, ref iterator));

            if (!tradeBids.TryGetFirstValue(index, out currentOffer, out iterator))
                return;

            do
            {
                currentBids.Add(currentOffer);
                //numBids += currentOffer.Units;
            } while (tradeBids.TryGetNextValue(out currentOffer, ref iterator));

            // Descending order (3, 2, 1). Normally left.CompareTo(right) for ascending order (1, 2, 3)
            currentAsks.AsArray().Sort(Comparer<Offer>.Create(
                (left, right) => right.Cost.CompareTo(left.Cost)));

            // Randomizing bids
            var n = currentBids.Length;
            while (n-- > 1)
            {
                var k = rng.NextInt(n + 1);
                var placeholder = currentBids[k];
                currentBids[k] = currentBids[n];
                currentBids[n] = placeholder;
            }

            while (currentBids.Length > 0 && currentAsks.Length > 0)
            {
                // Descending order
                var buyer = currentBids[currentBids.Length - 1];
                var seller = currentAsks[currentAsks.Length - 1];

                var quantityTraded = math.min(buyer.Units, seller.Units);
                var clearingPrice = seller.Cost;

                if (quantityTraded > 0)
                {
                    // Transferring goods
                    seller.Units -= quantityTraded;
                    buyer.Units -= quantityTraded;

                    currentAsks[currentAsks.Length - 1] = seller;
                    currentBids[currentBids.Length - 1] = buyer;

                    var targetInv = inventoryContents[seller.Source].AsNativeArray();
                    var placeholder = targetInv[index];
                    placeholder.Quantity =
                        math.clamp(placeholder.Quantity - quantityTraded, 0, placeholder.Quantity);
                    placeholder.RecordedPrice = (placeholder.Quantity * placeholder.RecordedPrice + quantityTraded
                                                 * clearingPrice) / (placeholder.Quantity + quantityTraded);
                    targetInv[index] = placeholder;

                    targetInv = inventoryContents[buyer.Source].AsNativeArray();
                    placeholder = targetInv[index];
                    placeholder.RecordedPrice = clearingPrice;
                    placeholder.Quantity += quantityTraded;
                    targetInv[index] = placeholder;

                    deltaMoney.Add(seller.Source, clearingPrice * quantityTraded);
                    deltaMoney.Add(buyer.Source, -clearingPrice * quantityTraded);

                    var memory = new float3(index, clearingPrice, currentFrame);
                    Debug.Log(memory);

                    goodHistory.Add(buyer.Source, memory);
                    goodHistory.Add(seller.Source, memory);
                }

                if (seller.Units <= 0)
                    currentAsks.RemoveAtSwapBack(currentAsks.Length - 1);

                if (buyer.Units <= 0)
                    currentBids.RemoveAtSwapBack(currentBids.Length - 1);
            }

            // TODO: History integration
        }
    }

    //[BurstCompile]
    private struct ProcessMoneyChanges : IJobForEachWithEntity<Wallet>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float> deltaMoney;

        [WriteOnly] public EntityCommandBuffer.Concurrent ecb;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, ref Wallet wallet)
        {
            if (!deltaMoney.TryGetFirstValue(entity, out var transaction, out var iterator))
                return;

            do
            {
                wallet.Money += transaction;
            } while (deltaMoney.TryGetNextValue(out transaction, ref iterator));

            if (wallet.Money > 0)
                return;

            // Bankrupt
            ecb.AddComponent(index, entity, new Bankrupt());
            Debug.Log("Bankrupt: " + entity.Index);
        }
    }

    [BurstCompile]
    private struct RewriteHistory : IJobForEachWithEntity<Agent>
    {
        [ReadOnly] public NativeMultiHashMap<Entity, float3> revisionism;
        [WriteOnly] public NativeMultiHashMap<Entity, float3>.Concurrent history;

        public void Execute([ReadOnly] Entity entity, [ReadOnly] int index, [ReadOnly] ref Agent throwaway)
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
    private struct ResetMultiHashMaps : IJob
    {
        public NativeMultiHashMap<int, Offer> tradeAsks, tradeBids;
        public NativeMultiHashMap<Entity, float> deltaMoney;
        public NativeMultiHashMap<Entity, float3> revisionism;

        public void Execute()
        {
            tradeAsks.Clear();
            tradeBids.Clear();
            deltaMoney.Clear();
            revisionism.Clear();
        }
    }

    [BurstCompile]
    private struct BurnOldHistory : IJob
    {
        public NativeMultiHashMap<Entity, float3> oldHistory;

        public void Execute()
        {
            oldHistory.Clear();
        }
    }
}