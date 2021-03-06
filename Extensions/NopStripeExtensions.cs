﻿using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.Stores;
using Nop.Services.Common;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Payments;
using Stripe;
using System;
using System.Collections.Generic;
using System.Text;
using stripe = Stripe;

namespace Nop.WebTail.Stripe.Extensions
{
    public static class NopStripeExtensions
    {
        //     An arbitrary string to be displayed on your customer's credit card statement.
        //     This may be up to 22 characters. As an example, if your website is RunClub and
        //     the item you're charging for is a race ticket, you may want to specify a statement_descriptor
        //     of RunClub 5K race ticket. The statement description may not include <>"' characters,
        //     and will appear on your customer's statement in capital letters. Non-ASCII characters
        //     are automatically stripped. While most banks display this information consistently,
        //     some may display it incorrectly or not at all.
        public static string ToStripeDescriptor(this string value, string prefix = null)
        {
            value = string.IsNullOrEmpty(prefix) ? value : $"{prefix}{value}";
            string subString = value.Length > 22 ? value.Substring(0, 21) : value;

            return subString.Replace("<", string.Empty)
                            .Replace(">", string.Empty)
                            .Replace("\"", string.Empty)
                            .Replace("*", string.Empty)
                            .Replace("'", string.Empty);
        }

        public static bool TryConnect(this StripePaymentSettings stripePaymentSettings)
        {
            try
            {
                var stripeService = new stripe.CustomerService(stripePaymentSettings.GetStripeClient());
                return stripeService.List() != null;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public static PaymentStatus GetPaymentStatus(this stripe.Charge charge, TransactionMode transactionMode)
        {
            switch (charge.GetStatus())
            {
                case StripeChargeStatus.Pending:
                    return PaymentStatus.Pending;
                case StripeChargeStatus.Succeeded:
                    return transactionMode == TransactionMode.Authorize ? PaymentStatus.Authorized : PaymentStatus.Paid;

                case StripeChargeStatus.Failed:
                default:
                    throw new ArgumentOutOfRangeException("StripeStatus provided unknown.");
            }
        }

        public static StripeChargeStatus GetStatus(this stripe.Charge charge)
        {
            switch (charge.Status)
            {
                case "succeeded":
                    return StripeChargeStatus.Succeeded;
                case "pending":
                    return StripeChargeStatus.Pending;
                case "failed":
                    return StripeChargeStatus.Failed;
                default:
                    throw new ArgumentOutOfRangeException("charge status unknown.");

            }
        }

        public static StripeRefundStatus GetStatus(this stripe.Refund refund)
        {
            switch (refund.Status)
            {
                case "succeeded":
                    return StripeRefundStatus.Succeeded;
                case "pending":
                    return StripeRefundStatus.Pending;
                case "failed":
                    return StripeRefundStatus.Failed;
                case "canceled":
                    return StripeRefundStatus.Canceled;
                default:
                    throw new ArgumentOutOfRangeException("refund status unknown.");

            }
        }
        
        public static stripe.TokenCreateOptions CreateTokenOptions(this ProcessPaymentRequest processPaymentRequest, Core.Domain.Customers.Customer customer, StripeCurrency stripeCurrency)
        {
            return new stripe.TokenCreateOptions()
            {
                Card = processPaymentRequest.CreateCreditCardOptions(customer, stripeCurrency)
            };
        }
        
        public static stripe.CreditCardOptions CreateCreditCardOptions(this ProcessPaymentRequest processPaymentRequest, Core.Domain.Customers.Customer customer, StripeCurrency stripeCurrency)
        {
            var creditCardOptions = new stripe.CreditCardOptions()
            {
                Cvc = processPaymentRequest.CreditCardCvv2,
                Currency = stripeCurrency.ToString(),
                ExpMonth = processPaymentRequest.CreditCardExpireMonth,
                ExpYear = processPaymentRequest.CreditCardExpireYear,
                Name = processPaymentRequest.CreditCardName,
                Number = processPaymentRequest.CreditCardNumber,
            };

            if (customer.BillingAddress != null)
            {
                creditCardOptions.AddressCity = customer.BillingAddress.City;
                creditCardOptions.AddressCountry = customer.BillingAddress.Country?.Name;
                creditCardOptions.AddressLine1 = customer.BillingAddress.Address1;
                creditCardOptions.AddressLine2 = customer.BillingAddress.Address2;
                creditCardOptions.AddressState = customer.BillingAddress.StateProvince?.Name;
                creditCardOptions.AddressZip = customer.BillingAddress.ZipPostalCode;
            }

            return creditCardOptions;
        }

        public static stripe.ChargeCreateOptions CreateChargeOptions(this ProcessPaymentRequest processPaymentRequest, Store store, stripe.Token token, TransactionMode transactionMode, StripeCurrency stripeCurrency)
        {

            var chargeRequest = new stripe.ChargeCreateOptions()
            {
                Amount = (int)processPaymentRequest.OrderTotal * 100,
                Capture = transactionMode == TransactionMode.Charge,
                Source = token.Id,
                StatementDescriptor = $"{store.Name.ToStripeDescriptor()}",
                Currency = stripeCurrency.ToString(),

            };
            return chargeRequest;
        }

        public static stripe.CustomerCreateOptions CreateCustomerOptions(this Core.Domain.Customers.Customer customer, StripePaymentSettings paymentSettings)
        {
            var customerCreateOptions = new stripe.CustomerCreateOptions()
            {
                Email = customer.Email,
            };

            customerCreateOptions.Metadata = new Dictionary<string, string>()
            {
                { paymentSettings.GetCustomerIdKey(), customer.Id.ToString() }
            };
                
            return customerCreateOptions;
        }

        public static stripe.Customer GetOrCreateCustomer(this stripe.CustomerService customerService, Core.Domain.Customers.Customer customer, IGenericAttributeService genericAttributeService, StripePaymentSettings paymentSettings)
        {
            string stripeCustomerId = genericAttributeService.GetAttribute<string>(customer, paymentSettings.GetCustomerIdKey());
            stripe.Customer result = customerService.GetOrCreateCustomer(customer, stripeCustomerId, paymentSettings);

            if (string.IsNullOrEmpty(stripeCustomerId))
                genericAttributeService.SaveAttribute(customer, paymentSettings.GetCustomerIdKey(), result.Id);

            return result;
        }

        public static stripe.Customer GetOrCreateCustomer(this stripe.CustomerService customerService, Core.Domain.Customers.Customer customer, string stripeCustomerId, StripePaymentSettings paymentSettings)
        {
            if (!string.IsNullOrEmpty(stripeCustomerId))
                return customerService.Get(stripeCustomerId);
            else
                return customerService.Create(customer.CreateCustomerOptions(paymentSettings));
        }

        public static stripe.Charge CreateCharge(this ProcessPaymentRequest processPaymentRequest, StripePaymentSettings stripePaymentSettings, CurrencySettings currencySettings, Store store, 
                                                      ICustomerService customerService, ICurrencyService currencyService, IGenericAttributeService genericAttributeService)
        {
            int substep = 0;

            try
            {
                var customer = customerService.GetCustomerById(processPaymentRequest.CustomerId);
                if (customer == null)
                    throw new NopException("Customer cannot be loaded");
                substep = 1;
                var currency = currencyService.GetCurrencyById(currencySettings.PrimaryStoreCurrencyId);
                if (currency == null)
                    throw new NopException("Primary store currency cannot be loaded");
                substep = 2;
                if (!Enum.TryParse(currency.CurrencyCode, out StripeCurrency stripeCurrency))
                    throw new NopException($"The {currency.CurrencyCode} currency is not supported by Stripe");
                substep = 3;

                var stripeCustomerService = new stripe.CustomerService(stripePaymentSettings.GetStripeClient());
                var chargeService = new stripe.ChargeService(stripePaymentSettings.GetStripeClient());
                var tokenService = new stripe.TokenService(stripePaymentSettings.GetStripeClient());
                substep = 4;
                var stripeCustomer = stripeCustomerService.GetOrCreateCustomer(customer, genericAttributeService, stripePaymentSettings);
                substep = 5;
                var tokenOptions = processPaymentRequest.CreateTokenOptions(customer, stripeCurrency);
                substep = 6;
                var token = tokenService.Create(tokenOptions);
                substep = 7;
                var chargeOptions = processPaymentRequest.CreateChargeOptions(store, token, stripePaymentSettings.TransactionMode, stripeCurrency);
                substep = 8;

                var charge = chargeService.Create(chargeOptions);
                substep = 9;
                return charge;
            } 
            catch (Exception ex)
            {
                throw new Exception($"Failed at substep {substep}", ex);
            }
            
        }

        public static stripe.Refund CreateRefund(this RefundPaymentRequest refundPaymentRequest, StripePaymentSettings stripePaymentSettings, CurrencySettings currencySettings, ICurrencyService currencyService)
        {
            var currency = currencyService.GetCurrencyById(currencySettings.PrimaryStoreCurrencyId);
            if (currency == null)
                throw new NopException("Primary store currency cannot be loaded");
            
            if (!Enum.TryParse(currency.CurrencyCode, out StripeCurrency stripeCurrency))
                throw new NopException($"The {currency.CurrencyCode} currency is not supported by Stripe");

            var refundService = new stripe.RefundService(stripePaymentSettings.GetStripeClient());
            stripe.Refund refund = refundService.Create(new stripe.RefundCreateOptions()
            {
                Amount = (int)refundPaymentRequest.AmountToRefund * 100,
                Charge = refundPaymentRequest.Order.CaptureTransactionId
            });

            return refund;
        }

        public static stripe.Charge CreateCapture(this CapturePaymentRequest capturePaymentRequest, StripePaymentSettings stripePaymentSettings, Store store)
        {
            var chargesService = new stripe.ChargeService(stripePaymentSettings.GetStripeClient());

            var chargeId = capturePaymentRequest.Order.AuthorizationTransactionId;
            stripe.Charge charge = chargesService.Capture(chargeId, new stripe.ChargeCaptureOptions()
            {
                StatementDescriptor = $"{store.Name.ToStripeDescriptor()}"
            });

            return charge;
            
        }
    }
}
