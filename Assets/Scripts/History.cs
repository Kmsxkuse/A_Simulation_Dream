using System.Collections.Generic;

public static class History
{
    public static List<float>[] Prices;
    public static List<float>[] Asks;
    public static List<float>[] Bids;
    public static List<float>[] Trades;
    public static List<float>[] Profits;

    public static int GoodsCount;

    public static void AssignLogs(int goodsNum)
    {
        GoodsCount = goodsNum;

        Prices = new List<float>[goodsNum];
        for (var i = 0; i < Prices.Length; i++)
            Prices[i] = new List<float>();

        Asks = new List<float>[goodsNum];
        for (var i = 0; i < Asks.Length; i++)
            Asks[i] = new List<float>();

        Bids = new List<float>[goodsNum];
        for (var i = 0; i < Bids.Length; i++)
            Bids[i] = new List<float>();

        Trades = new List<float>[goodsNum];
        for (var i = 0; i < Trades.Length; i++)
            Trades[i] = new List<float>();

        Profits = new List<float>[goodsNum];
        for (var i = 0; i < Profits.Length; i++)
            Profits[i] = new List<float>();
    }
}