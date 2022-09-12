using FreeSql;
using HDWallet.Tron;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;
using SkiaSharp.QrCode.Image;
using System.Diagnostics;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Models;

namespace TokenPay.Controllers
{
    [Route("{action}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class HomeController : Controller
    {
        private readonly IBaseRepository<TokenOrders> _repository;
        private readonly IBaseRepository<TokenRate> _rateRepository;
        private readonly IBaseRepository<Tokens> _tokenRepository;
        private readonly IConfiguration _configuration;
        private int Decimals => _configuration.GetValue("Decimals", 4);
        public HomeController(IBaseRepository<TokenOrders> repository,
            IBaseRepository<TokenRate> rateRepository,
            IBaseRepository<Tokens> tokenRepository,
            IConfiguration configuration)
        {
            this._repository = repository;
            this._rateRepository = rateRepository;
            this._tokenRepository = tokenRepository;
            this._configuration = configuration;
        }
        [Route("/")]
        public IActionResult Index()
        {
            return View();
        }

        public async Task<IActionResult> Pay(Guid Id)
        {
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return Content("订单不存在！");
            }
            ViewData["QrCode"] = Convert.ToBase64String(CreateQrCode(order.ToAddress));
            var ExpireTime = _configuration.GetValue("ExpireTime", 10 * 60);
            ViewData["ExpireTime"] = order.CreateTime.AddSeconds(ExpireTime);
            return View(order);
        }
        [Route("/{action}/{id}")]
        public async Task<IActionResult> Check(Guid Id)
        {
            var order = await _repository.Where(x => x.Id == Id).FirstAsync();
            if (order == null)
            {
                return Content(OrderStatus.Pending.ToString());
            }
            return Content(order.Status.ToString());
        }
        /// <summary>
        /// 创建订单
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("/" + nameof(CreateOrder))]
        [ApiExplorerSettings(IgnoreApi = false)]
        public async Task<IActionResult> CreateOrder([FromForm] CreateOrderViewModel model)
        {
            if (!ModelState.IsValid)
            {
                string messages = string.Join("; ", ModelState.Values
                                        .SelectMany(x => x.Errors)
                                        .Select(x => x.ErrorMessage));

                return Json(new ReturnData
                {
                    Message = messages
                });
            }
            if (model.ActualAmount <= 0)
            {
                return Json(new ReturnData
                {
                    Message = "金额有误！"
                });
            }
            //订单号已存在
            var hasOrder = await _repository.Where(x => x.OutOrderId == model.OutOrderId).FirstAsync();
            if (hasOrder != null)
            {
                return Json(new ReturnData<string>
                {
                    Success = true,
                    Message = "订单已存在，查询旧订单！",
                    Data = Host + Url.Action(nameof(Pay), new { Id = hasOrder.Id })
                });
            }
            var order = new TokenOrders
            {
                OutOrderId = model.OutOrderId,
                Status = OrderStatus.Pending,
                Currency = model.Currency,
                ActualAmount = model.ActualAmount,
                NotifyUrl = model.NotifyUrl,
                RedirectUrl = model.RedirectUrl
            };
            var UseDynamicAddress = _configuration.GetValue("UseDynamicAddress", true);
            try
            {
                if (UseDynamicAddress)
                {
                    var (Address, Amount) = await GetUseTokenDynamicAdress(model);
                    order.ToAddress = Address;
                    order.Amount = Amount;
                }
                else
                {
                    var (Address, Amount) = await GetUseTokenStaticAdress(model);
                    order.ToAddress = Address;
                    order.Amount = Amount;
                }
            }
            catch (TokenPayException e)
            {
                return Json(new ReturnData
                {
                    Message = e.Message
                });
            }
            await _repository.InsertAsync(order);
            return Json(new ReturnData<string>
            {
                Success = true,
                Message = "创建订单成功！",
                Data = Host + Url.Action(nameof(Pay), new { Id = order.Id })
            });
        }
        private string Host
        {
            get
            {
                var host = _configuration.GetValue<string>("WebSiteUrl");
                if (string.IsNullOrEmpty(host))
                {
                    host = $"{Request.Scheme}://{Request.Host}";
                }
                return host;
            }
        }
        /// <summary>
        /// 动态地址
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        private async Task<(string, decimal)> GetUseTokenDynamicAdress(CreateOrderViewModel model)
        {
            var (UseTokenAdress, _) = await CreateTronWallet(model.OrderUserKey);
            var rate = _configuration.GetValue("Rate", 0m);
            if (rate <= 0)
            {
                rate = await _rateRepository.Where(x => x.Currency == model.Currency && x.FiatCurrency == FiatCurrency.CNY).FirstAsync(x => x.Rate);
            }
            if (rate <= 0)
            {
                throw new TokenPayException("汇率有误！");
            }
            var Amount = (model.ActualAmount / rate).ToRound(Decimals);
            return (UseTokenAdress, Amount);
        }
        /// <summary>
        /// 根据唯一Id获取一个地址
        /// </summary>
        /// <param name="OrderUserKey"></param>
        /// <returns></returns>
        /// <exception cref="TokenPayException"></exception>
        private async Task<(string, string)> CreateTronWallet(string OrderUserKey)
        {
            if (string.IsNullOrEmpty(OrderUserKey))
            {
                throw new TokenPayException("动态地址需传递用户标识！");
            }

            var token = await _tokenRepository.Where(x => x.Id == OrderUserKey).FirstAsync();
            if (token == null)
            {
                var ecKey = Nethereum.Signer.EthECKey.GenerateKey();
                var rawPrivateKey = ecKey.GetPrivateKeyAsBytes();
                var hex = Convert.ToHexString(rawPrivateKey);
                var tronWallet = new TronWallet(hex);
                var Address = tronWallet.Address;
                token = new Tokens
                {
                    Id = OrderUserKey,
                    Address = Address,
                    Key = hex
                };
                await _tokenRepository.InsertAsync(token);
            }
            return (token.Address, token.Key);
        }
        /// <summary>
        /// 静态地址
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        /// <exception cref="TokenPayException"></exception>
        private async Task<(string, decimal)> GetUseTokenStaticAdress(CreateOrderViewModel model)
        {
            var USDT_TRC20 = _configuration.GetSection("USDT-TRC20-Address").Get<string[]>();

            var CurrentAdress = model.Currency switch
            {
                Currency.USDT_TRC20 => USDT_TRC20,
                _ => new string[0]
            };
            if (CurrentAdress.Length == 0)
            {
                throw new TokenPayException("未配置收款地址！");
            }
            var rate = _configuration.GetValue("Rate", 0m);
            if (rate <= 0)
            {
                rate = await _rateRepository.Where(x => x.Currency == model.Currency && x.FiatCurrency == FiatCurrency.CNY).FirstAsync(x => x.Rate);
            }
            if (rate <= 0)
            {
                throw new TokenPayException("汇率有误！");
            }
            var Amount = (model.ActualAmount / rate).ToRound(Decimals);
            //随机排序所有收款地址
            CurrentAdress = CurrentAdress.OrderBy(x => Guid.NewGuid()).ToArray();
            var UseTokenAdress = string.Empty;
            foreach (var token in CurrentAdress)
            {
                //判断是否存在此金额、此地址、此币种的待付款
                var has = await _repository
                    .Where(x => x.ToAddress == token)
                    //.Where(x => x.ActualAmount == order.ActualAmount) //原始金额
                    .Where(x => x.Currency == model.Currency)//虚拟币币种
                    .Where(x => x.Amount == Amount) //实际支付的虚拟币金额
                    .Where(x => x.Status == OrderStatus.Pending) //代支付
                    .AnyAsync();
                if (!has)
                {
                    UseTokenAdress = token;
                    break;
                }
            }
            //所有地址都存在此金额
            if (string.IsNullOrEmpty(UseTokenAdress))
            {
                var AddAmount = 0.0001m;//初始递增量
                for (int i = 0; i < 1000; i++)//最多递增100次
                {
                    foreach (var token in CurrentAdress)
                    {
                        //判断是否存在此金额、此地址、此币种的待付款
                        var has = await _repository
                            .Where(x => x.ToAddress == token)
                            //.Where(x => x.ActualAmount == order.ActualAmount) //原始金额
                            .Where(x => x.Currency == model.Currency)//虚拟币币种
                            .Where(x => x.Amount == Amount + AddAmount) //实际支付的虚拟币金额
                            .Where(x => x.Status == OrderStatus.Pending) //代支付
                            .AnyAsync();
                        if (!has)
                        {
                            UseTokenAdress = token;
                            Amount = Amount + AddAmount;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(UseTokenAdress))
                    {
                        break;
                    }
                    AddAmount += 0.0001m;
                }
            }
            if (string.IsNullOrEmpty(UseTokenAdress))
            {
                throw new TokenPayException("无可用收款地址！");
            }
            return (UseTokenAdress, Amount);
        }
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            var context = HttpContext.Features.Get<IExceptionHandlerFeature>();
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier, Message = context?.Error?.Message });
        }

        /// <summary>
        /// 创建二维码
        /// </summary>
        /// <param name="qrcode"></param>
        /// <returns></returns>
        private static byte[] CreateQrCode(string qrcode)
        {
            using var stream = new MemoryStream();
            var qrCode = new QrCode(qrcode, new Vector2Slim(256, 256), SKEncodedImageFormat.Png);
            qrCode.GenerateImage(stream);
            return stream.ToArray();
        }
    }
}