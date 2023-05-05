using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Perception.Randomization.Samplers;

namespace UnityEngine.Perception.Randomization.Parameters
{
    /// <summary>
    /// Parameters, in conjunction with a parameter configuration, are used to create convenient interfaces for
    /// randomizing simulations.
    /// </summary>
    [Serializable]
    public abstract class Parameter
    {
        [HideInInspector, SerializeField] internal bool collapsed;

        /// <summary>
        /// The sample type generated by this parameter
        /// </summary>
        public abstract Type sampleType { get; }

        /// <summary>
        /// Returns an IEnumerable that iterates over each sampler field in this parameter
        /// </summary>
        public abstract IEnumerable<ISampler> samplers { get; }

        /// <summary>
        /// Returns the display name of a parameter type
        /// </summary>
        /// <param name="type">A subclass of Parameter</param>
        /// <returns>The parameter type's display name</returns>
        public static string GetDisplayName(Type type)
        {
            return type.Name.Replace("Parameter", "");
        }

        /// <summary>
        /// Generates a generic sample
        /// </summary>
        /// <returns>The generated sample</returns>
        public abstract object GenericSample();

        /// <summary>
        /// Validates parameter settings
        /// </summary>
        public virtual void Validate() {}
    }
}