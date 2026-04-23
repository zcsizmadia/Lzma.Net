// SPDX-License-Identifier: 0BSD

namespace LzmaNet;

/// <summary>
/// Represents errors that occur during LZMA/XZ compression or decompression.
/// </summary>
public class LzmaException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaException"/> class.
    /// </summary>
    public LzmaException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public LzmaException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public LzmaException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The exception that is thrown when compressed data is corrupt or truncated.
/// </summary>
public class LzmaDataErrorException : LzmaException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaDataErrorException"/> class.
    /// </summary>
    public LzmaDataErrorException() : base("Compressed data is corrupt.") { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaDataErrorException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public LzmaDataErrorException(string message) : base(message) { }
}

/// <summary>
/// The exception that is thrown when the input data format is not recognized.
/// </summary>
public class LzmaFormatException : LzmaException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaFormatException"/> class.
    /// </summary>
    public LzmaFormatException() : base("Input format not recognized.") { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LzmaFormatException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public LzmaFormatException(string message) : base(message) { }
}
