namespace Mapache.Infrastructure.Llm;

/// <summary>El proveedor no está configurado o rechazó la petición antes de empezar a responder.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
