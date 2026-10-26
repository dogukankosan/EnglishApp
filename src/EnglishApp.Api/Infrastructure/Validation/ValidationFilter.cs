using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EnglishApp.Api.Infrastructure.Validation;

/// <summary>
/// Controller'a gelen her parametre için kayıtlı FluentValidation doğrulayıcısını çalıştırır.
/// Hata varsa ValidationException fırlatır → ExceptionMiddleware standart 400 yanıtı döner.
/// </summary>
public sealed class ValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (services.GetService(validatorType) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            if (!result.IsValid) throw new ValidationException(result.Errors);
        }

        await next();
    }
}

public static class ValidationRules
{
    /// <summary>Şifre politikası: en az 8 karakter, en az 1 harf ve 1 rakam.</summary>
    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Şifre boş olamaz.")
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalı.")
            .MaximumLength(100).WithMessage("Şifre en fazla 100 karakter olabilir.")
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit)).WithMessage("Şifre en az 1 harf ve 1 rakam içermeli.");

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("E-posta boş olamaz.")
            .MaximumLength(256).WithMessage("E-posta en fazla 256 karakter olabilir.")
            .EmailAddress().WithMessage("Geçerli bir e-posta adresi girin.");

    public static IRuleBuilderOptions<T, string> ValidFullName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Ad soyad boş olamaz.")
            .MaximumLength(100).WithMessage("Ad soyad en fazla 100 karakter olabilir.");
}
