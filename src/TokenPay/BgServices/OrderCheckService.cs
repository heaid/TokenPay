using Flurl;
using Flurl.Http;
using FreeSql;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Models.TronModel;

namespace TokenPay.BgServices
{
    public class OrderCheckService : BaseScheduledService
    {
        private readonly ILogger<OrderCheckService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment env;
        private readonly IServiceProvider _serviceProvider;

        public OrderCheckService(ILogger<OrderCheckService> logger,
            IConfiguration configuration,
            IHostEnvironment env,
            IServiceProvider serviceProvider) : base("订单检测", TimeSpan.FromSeconds(3), logger)
        {
            _logger = logger;
            this._configuration = configuration;
            this.env = env;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync()
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            var _repository = scope.ServiceProvider.GetRequiredService<IBaseRepository<TokenOrders>>();

            var Address = await _repository
                .Where(x => x.Status == OrderStatus.Pending)
                .Where(x => x.Currency == Currency.USDT_TRC20)
                .ToListAsync(x => x.ToAddress);
            if (Address.Count > 0)
                _logger.LogInformation("待检测地址数：{c}", Address.Count);
            var ContractAddress = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
            var BaseUrl = "https://api.trongrid.io";
            if (!env.IsProduction())
            {
                ContractAddress = "TG3XXyExBkPp9nzdajDZsozEu4BkaSJozs";
                BaseUrl = "https://api.shasta.trongrid.io";
            }
            var OnlyConfirmed = _configuration.GetValue("OnlyConfirmed", true);
            var start = DateTime.Now.AddMinutes(-10);
            foreach (var address in Address)
            {
                //查询此地址待支付订单
                var orders = await _repository
                    .Where(x => x.Status == OrderStatus.Pending)
                    .Where(x => x.Currency == Currency.USDT_TRC20)
                    .Where(x => x.ToAddress == address)
                    .OrderBy(x => x.CreateTime)
                    .ToListAsync();
                if (!orders.Any())
                {
                    continue;
                }
                var query = new Dictionary<string, object>();
                if (OnlyConfirmed)
                {
                    query.Add("only_confirmed", true);
                }
                else
                {
                    query.Add("only_unconfirmed", true);
                }
                query.Add("only_to", true);
                query.Add("limit", 50);
                query.Add("min_timestamp", start.ToUnixTimeStamp());
                query.Add("contract_address", ContractAddress);
                var req = BaseUrl
                    .AppendPathSegment($"v1/accounts/{address}/transactions/trc20")
                    .WithTimeout(15);
                if (!env.IsProduction())
                    req = req.WithHeader("TRON-PRO-API-KEY", _configuration.GetValue("TRON-PRO-API-KEY", ""));
                var result = await req
                    .GetJsonAsync<BaseResponse<Transactions>>();

                if (result.Success && result.Data?.Count > 0)
                {
                    foreach (var item in result.Data)
                    {
                        //合约地址不匹配
                        if (item.TokenInfo?.Address != ContractAddress)
                        {
                            continue;
                        }
                        //没有需要匹配的订单了
                        if (!orders.Any())
                        {
                            break;
                        }
                        //此交易已被其他订单使用
                        if (await _repository.Select.AnyAsync(x => x.BlockTransactionId == item.TransactionId))
                        {
                            continue;
                        }
                        var order = orders.Where(x => x.Amount == item.Amount && x.ToAddress == item.To && x.CreateTime < item.BlockTimestamp.ToDateTime())
                            .OrderByDescending(x=>x.CreateTime)//优先付最后一单
                            .FirstOrDefault();
                        if (order != null)
                        {
                            order.FromAddress = item.From;
                            order.BlockTransactionId = item.TransactionId;
                            order.Status = OrderStatus.Paid;
                            order.PayTime = DateTime.Now;
                            await _repository.UpdateAsync(order);
                            orders.Remove(order);
                        }
                    }
                }
            }
        }


        private async Task<bool> Notify(TokenOrders order)
        {
            if (!string.IsNullOrEmpty(order.NotifyUrl))
            {
                try
                {
                    var result = await order.NotifyUrl.PostJsonAsync(order);
                    var message = await result.GetStringAsync();
                    if (result.StatusCode == 200)
                    {
                        _logger.LogInformation("订单异步通知成功！\n{msg}", message);
                        return true;
                    }
                    else
                    {
                        _logger.LogInformation("订单异步通知失败：{msg}", message);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogInformation("订单异步通知失败：{msg}", e.Message);
                }
            }
            return false;
        }
    }
}