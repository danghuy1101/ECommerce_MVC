using ECommerceMVC.Data;
using ECommerceMVC.Helpers;
using ECommerceMVC.Services;
using ECommerceMVC.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECommerceMVC.Controllers
{
	public class CartController : Controller
	{
		private readonly PaypalClient _paypalClient;
		private readonly Hshop2023Context db;
		private readonly IVnPayService _vnPayservice;

		public CartController(Hshop2023Context context, PaypalClient paypalClient, IVnPayService vnPayservice)
		{
			_paypalClient = paypalClient;
			db = context;
			_vnPayservice = vnPayservice;
		}

		public List<CartItem> Cart => HttpContext.Session.Get<List<CartItem>>(MySetting.CART_KEY) ?? new List<CartItem>();

		public IActionResult Index()
		{
			return View(Cart);
		}

		public IActionResult AddToCart(int id, int quantity = 1)
		{
			var gioHang = Cart;
			var item = gioHang.SingleOrDefault(p => p.MaHh == id);
			if (item == null)
			{
				var hangHoa = db.HangHoas.SingleOrDefault(p => p.MaHh == id);
				if (hangHoa == null)
				{
					TempData["Message"] = $"Không tìm thấy hàng hóa có mã {id}";
					return Redirect("/404");
				}
				item = new CartItem
				{
					MaHh = hangHoa.MaHh,
					TenHH = hangHoa.TenHh,
					DonGia = hangHoa.DonGia ?? 0,
					Hinh = hangHoa.Hinh ?? string.Empty,
					SoLuong = quantity
				};
				gioHang.Add(item);
			}
			else
			{
				item.SoLuong += quantity;
			}

			HttpContext.Session.Set(MySetting.CART_KEY, gioHang);
			return RedirectToAction("Index");
		}

		public IActionResult RemoveCart(int id)
		{
			var gioHang = Cart;
			var item = gioHang.SingleOrDefault(p => p.MaHh == id);
			if (item != null)
			{
				gioHang.Remove(item);
				HttpContext.Session.Set(MySetting.CART_KEY, gioHang);
			}
			return RedirectToAction("Index");
		}

		[Authorize]
		[HttpGet]
		public IActionResult Checkout()
		{
			if (Cart.Count == 0)
			{
				return Redirect("/");
			}

			ViewBag.PaypalClientdId = _paypalClient.ClientId;
			return View(Cart);
		}

		[Authorize]
		[HttpPost]
		public IActionResult Checkout(CheckoutVM model, string payment = "COD")
		{
			if (ModelState.IsValid)
			{
				if (payment == "Thanh toán VNPay")
				{
					var vnPayModel = new VnPaymentRequestModel
					{
						Amount = Cart.Sum(p => p.ThanhTien),
						CreatedDate = DateTime.Now,
						Description = $"{model.HoTen} {model.DienThoai}",
						FullName = model.HoTen,
						OrderId = new Random().Next(1000, 100000)
					};
					return Redirect(_vnPayservice.CreatePaymentUrl(HttpContext, vnPayModel));
				}

				var customerId = HttpContext.User.Claims.SingleOrDefault(p => p.Type == MySetting.CLAIM_CUSTOMERID).Value;
				var khachHang = new KhachHang();
				if (model.GiongKhachHang)
				{
					khachHang = db.KhachHangs.SingleOrDefault(kh => kh.MaKh == customerId);
				}

				var hoadon = new HoaDon
				{
					MaKh = customerId,
					HoTen = model.HoTen ?? khachHang.HoTen,
					DiaChi = model.DiaChi ?? khachHang.DiaChi,
					DienThoai = model.DienThoai ?? khachHang.DienThoai,
					NgayDat = DateTime.Now,
					CachThanhToan = "COD",
					CachVanChuyen = "GRAB",
					MaTrangThai = 1, //Đã thanh toán thành công
					GhiChu = model.GhiChu
				};

				db.Database.BeginTransaction();

				try
				{
					db.Add(hoadon);
					db.SaveChanges();

					var cthds = new List<ChiTietHd>();
					foreach (var item in Cart)
					{
						cthds.Add(new ChiTietHd
						{
							MaHd = hoadon.MaHd,
							SoLuong = item.SoLuong,
							DonGia = item.DonGia,
							MaHh = item.MaHh,
							GiamGia = 0
						});
					}
					db.AddRange(cthds);
					db.SaveChanges();
					db.Database.CommitTransaction();

					HttpContext.Session.Set<List<CartItem>>(MySetting.CART_KEY, new List<CartItem>());

					return View("Success");
				}
				catch
				{
					db.Database.RollbackTransaction();
				}
			}
			return View(Cart);
		}

		[Authorize]
		public IActionResult PaymentSuccess()
		{
			return View("Success");
		}

		#region Paypal payment
		[Authorize]
		[HttpPost("/Cart/create-paypal-order")]
		public async Task<IActionResult> CreatePaypalOrder(CancellationToken cancellationToken)
		{
			// Thông tin đơn hàng gửi qua Paypal
			var tongTien = Cart.Sum(p => p.ThanhTien).ToString();
			var donViTienTe = "USD";
			var maDonHangThamChieu = "DH" + DateTime.Now.Ticks.ToString();

			try
			{
				var response = await _paypalClient.CreateOrder(tongTien, donViTienTe, maDonHangThamChieu);

				return Ok(response);
			}
			catch (Exception ex)
			{
				var error = new { ex.GetBaseException().Message };
				return BadRequest(error);
			}
		}

		[Authorize]
		[HttpPost("/Cart/capture-paypal-order")]
		public async Task<IActionResult> CapturePaypalOrder(string orderID, string ghiChu, CancellationToken cancellationToken)
		{
			try
			{
				// Capture đơn hàng từ PayPal
				var response = await _paypalClient.CaptureOrder(orderID);

				// Nếu giao dịch thành công, tiến hành lưu vào cơ sở dữ liệu
				if (response.status == "COMPLETED")
				{
					// Lấy thông tin khách hàng từ context
					var customerId = HttpContext.User.Claims.SingleOrDefault(p => p.Type == MySetting.CLAIM_CUSTOMERID).Value;
					var khachHang = db.KhachHangs.SingleOrDefault(kh => kh.MaKh == customerId);

					// Tạo mới hóa đơn
					var hoadon = new HoaDon
					{
						MaKh = customerId,
						HoTen = khachHang?.HoTen,
						DiaChi = khachHang?.DiaChi,
						DienThoai = khachHang?.DienThoai,
						NgayDat = DateTime.Now,
						CachThanhToan = "PayPal",
						CachVanChuyen = "GRAB",
						MaTrangThai = 1, // Đã thanh toán thành công
						GhiChu = "Thanh toán qua Paypal"
					};

					// Bắt đầu giao dịch
					db.Database.BeginTransaction();
					try
					{
						// Lưu hóa đơn vào cơ sở dữ liệu
						db.Add(hoadon);
						db.SaveChanges();

						// Tạo chi tiết hóa đơn từ giỏ hàng
						var cthds = new List<ChiTietHd>();
						foreach (var item in Cart)
						{
							cthds.Add(new ChiTietHd
							{
								MaHd = hoadon.MaHd,
								SoLuong = item.SoLuong,
								DonGia = item.DonGia,
								MaHh = item.MaHh,
								GiamGia = 0
							});
						}
						db.AddRange(cthds);
						db.SaveChanges();

						// Commit giao dịch
						db.Database.CommitTransaction();

						// Xóa giỏ hàng sau khi thanh toán thành công
						HttpContext.Session.Set<List<CartItem>>(MySetting.CART_KEY, new List<CartItem>());

						return Ok(response);
					}
					catch (Exception ex)
					{
						// Rollback giao dịch nếu có lỗi
						db.Database.RollbackTransaction();
						var error = new { ex.GetBaseException().Message };
						return BadRequest(error);
					}
				}
				// Nếu trạng thái không phải là "COMPLETED", trả về lỗi
				return BadRequest(new { message = "Thanh toán PayPal không thành công." });
			}
			catch (Exception ex)
			{
				// Xử lý lỗi từ PayPal
				var error = new { ex.GetBaseException().Message };
				return BadRequest(error);
			}
		}

		#endregion

		[Authorize]
		public IActionResult PaymentFail()
		{
			return View();
		}

		[Authorize]
		public IActionResult PaymentCallBack()
		{
			var response = _vnPayservice.PaymentExecute(Request.Query);

			if (response == null || response.VnPayResponseCode != "00")
			{
				TempData["Message"] = $"Lỗi thanh toán VN Pay: {response.VnPayResponseCode}";
				return RedirectToAction("PaymentFail");
			}

			// Lấy thông tin khách hàng từ context
			var customerId = HttpContext.User.Claims.SingleOrDefault(p => p.Type == MySetting.CLAIM_CUSTOMERID).Value;
			var khachHang = db.KhachHangs.SingleOrDefault(kh => kh.MaKh == customerId);

			// Tạo mới hóa đơn
			var hoadon = new HoaDon
			{
				MaKh = customerId,
				HoTen = khachHang?.HoTen,
				DiaChi = khachHang?.DiaChi,
				DienThoai = khachHang?.DienThoai,
				NgayDat = DateTime.Now,
				CachThanhToan = "VNPAY",
				CachVanChuyen = "GRAB",
				MaTrangThai = 1, // Đã thanh toán thành công
				GhiChu = "Thanh toán qua VNPay"
			};

			// Bắt đầu giao dịch
			db.Database.BeginTransaction();
			try
			{
				// Lưu hóa đơn vào cơ sở dữ liệu
				db.Add(hoadon);
				db.SaveChanges();

				// Tạo chi tiết hóa đơn từ giỏ hàng
				var cthds = new List<ChiTietHd>();
				foreach (var item in Cart)
				{
					cthds.Add(new ChiTietHd
					{
						MaHd = hoadon.MaHd,
						SoLuong = item.SoLuong,
						DonGia = item.DonGia,
						MaHh = item.MaHh,
						GiamGia = 0
					});
				}
				db.AddRange(cthds);
				db.SaveChanges();

				// Commit giao dịch
				db.Database.CommitTransaction();

				// Xóa giỏ hàng sau khi thanh toán thành công
				HttpContext.Session.Set<List<CartItem>>(MySetting.CART_KEY, new List<CartItem>());
				TempData["Message"] = $"Thanh toán VNPay thành công";
				return RedirectToAction("PaymentSuccess");
			}
			catch (Exception ex)
			{
				// Rollback giao dịch nếu có lỗi
				db.Database.RollbackTransaction();
				var error = new { ex.GetBaseException().Message };
				return BadRequest(error);
			}
		}
	}
}
