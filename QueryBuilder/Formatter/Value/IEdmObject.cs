﻿using Microsoft.OData.Edm;

namespace ODataQueryBuilder.Formatter.Value
{
    /// <summary>
    /// Represents an instance of an <see cref="IEdmType"/>.
    /// </summary>
    public interface IEdmObject
    {
        /// <summary>
        /// Gets the <see cref="IEdmTypeReference"/> of this instance.
        /// </summary>
        /// <returns>The <see cref="IEdmTypeReference"/> of this instance.</returns>
        IEdmTypeReference GetEdmType();
    }
}
