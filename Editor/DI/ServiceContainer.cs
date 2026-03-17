// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.Linq;

namespace GameBooom.Editor.DI
{
    internal enum ServiceLifetime { Singleton, Scoped, Transient }

    internal class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public Func<IServiceProvider, object> Factory { get; set; }
        public object Instance { get; set; }
        public ServiceLifetime Lifetime { get; set; }
    }

    internal class ServiceCollection
    {
        private readonly List<ServiceDescriptor> _descriptors = new List<ServiceDescriptor>();

        public ServiceCollection AddSingleton<TService, TImplementation>() where TImplementation : TService
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                ImplementationType = typeof(TImplementation),
                Lifetime = ServiceLifetime.Singleton
            });
            return this;
        }

        public ServiceCollection AddSingleton<TService>(TService instance)
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Instance = instance,
                Lifetime = ServiceLifetime.Singleton
            });
            return this;
        }

        public ServiceCollection AddSingleton<TService>(Func<IServiceProvider, TService> factory)
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Factory = sp => factory(sp),
                Lifetime = ServiceLifetime.Singleton
            });
            return this;
        }

        public ServiceCollection AddScoped<TService, TImplementation>() where TImplementation : TService
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                ImplementationType = typeof(TImplementation),
                Lifetime = ServiceLifetime.Scoped
            });
            return this;
        }

        public ServiceCollection AddScoped<TService>(Func<IServiceProvider, TService> factory)
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Factory = sp => factory(sp),
                Lifetime = ServiceLifetime.Scoped
            });
            return this;
        }

        public ServiceCollection AddTransient<TService, TImplementation>() where TImplementation : TService
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                ImplementationType = typeof(TImplementation),
                Lifetime = ServiceLifetime.Transient
            });
            return this;
        }

        public ServiceCollection AddTransient<TService>(Func<IServiceProvider, TService> factory)
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Factory = sp => factory(sp),
                Lifetime = ServiceLifetime.Transient
            });
            return this;
        }

        public ServiceProvider BuildServiceProvider()
        {
            return new ServiceProvider(_descriptors.ToList());
        }
    }

    internal class ServiceProvider : IServiceProvider, IDisposable
    {
        private readonly List<ServiceDescriptor> _descriptors;
        private readonly Dictionary<Type, object> _singletonInstances = new Dictionary<Type, object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly object _lock = new object();

        public ServiceProvider(List<ServiceDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public object GetService(Type serviceType)
        {
            var descriptor = _descriptors.LastOrDefault(d => d.ServiceType == serviceType);
            if (descriptor == null) return null;

            switch (descriptor.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    return GetOrCreateSingleton(descriptor);
                case ServiceLifetime.Scoped:
                    return GetOrCreateSingleton(descriptor); // In root scope, scoped = singleton
                case ServiceLifetime.Transient:
                    return CreateInstance(descriptor);
                default:
                    return null;
            }
        }

        public T GetService<T>() => (T)GetService(typeof(T));

        public T GetRequiredService<T>()
        {
            var service = GetService<T>();
            if (service == null)
                throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered.");
            return service;
        }

        public ServiceScope CreateScope()
        {
            return new ServiceScope(this, _descriptors);
        }

        private object GetOrCreateSingleton(ServiceDescriptor descriptor)
        {
            lock (_lock)
            {
                if (_singletonInstances.TryGetValue(descriptor.ServiceType, out var existing))
                    return existing;

                var instance = CreateInstance(descriptor);
                if (instance != null)
                {
                    _singletonInstances[descriptor.ServiceType] = instance;
                    if (instance is IDisposable disposable)
                        _disposables.Add(disposable);
                }
                return instance;
            }
        }

        private object CreateInstance(ServiceDescriptor descriptor)
        {
            if (descriptor.Instance != null)
                return descriptor.Instance;

            if (descriptor.Factory != null)
                return descriptor.Factory(this);

            if (descriptor.ImplementationType != null)
                return ActivateType(descriptor.ImplementationType);

            return null;
        }

        internal object ActivateType(Type type)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                return Activator.CreateInstance(type);

            var ctor = constructors.OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = GetService(parameters[i].ParameterType);
                if (args[i] == null && !parameters[i].HasDefaultValue)
                    throw new InvalidOperationException(
                        $"Cannot resolve parameter '{parameters[i].Name}' of type '{parameters[i].ParameterType.Name}' for '{type.Name}'.");
                if (args[i] == null)
                    args[i] = parameters[i].DefaultValue;
            }

            return ctor.Invoke(args);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var disposable in _disposables)
                {
                    try { disposable.Dispose(); } catch { }
                }
                _disposables.Clear();
                _singletonInstances.Clear();
            }
        }
    }

    internal class ServiceScope : IServiceProvider, IDisposable
    {
        private readonly ServiceProvider _rootProvider;
        private readonly List<ServiceDescriptor> _descriptors;
        private readonly Dictionary<Type, object> _scopedInstances = new Dictionary<Type, object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly object _lock = new object();

        public ServiceScope(ServiceProvider rootProvider, List<ServiceDescriptor> descriptors)
        {
            _rootProvider = rootProvider;
            _descriptors = descriptors;
        }

        public object GetService(Type serviceType)
        {
            var descriptor = _descriptors.LastOrDefault(d => d.ServiceType == serviceType);
            if (descriptor == null) return null;

            switch (descriptor.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    return _rootProvider.GetService(serviceType);
                case ServiceLifetime.Scoped:
                    return GetOrCreateScoped(descriptor);
                case ServiceLifetime.Transient:
                    return CreateInstance(descriptor);
                default:
                    return null;
            }
        }

        public T GetService<T>() => (T)GetService(typeof(T));

        public T GetRequiredService<T>()
        {
            var service = GetService<T>();
            if (service == null)
                throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered.");
            return service;
        }

        private object GetOrCreateScoped(ServiceDescriptor descriptor)
        {
            lock (_lock)
            {
                if (_scopedInstances.TryGetValue(descriptor.ServiceType, out var existing))
                    return existing;

                var instance = CreateInstance(descriptor);
                if (instance != null)
                {
                    _scopedInstances[descriptor.ServiceType] = instance;
                    if (instance is IDisposable disposable)
                        _disposables.Add(disposable);
                }
                return instance;
            }
        }

        private object CreateInstance(ServiceDescriptor descriptor)
        {
            if (descriptor.Instance != null)
                return descriptor.Instance;

            if (descriptor.Factory != null)
                return descriptor.Factory(this);

            if (descriptor.ImplementationType != null)
                return ActivateType(descriptor.ImplementationType);

            return null;
        }

        private object ActivateType(Type type)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                return Activator.CreateInstance(type);

            var ctor = constructors.OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = GetService(parameters[i].ParameterType);
                if (args[i] == null && !parameters[i].HasDefaultValue)
                    throw new InvalidOperationException(
                        $"Cannot resolve parameter '{parameters[i].Name}' of type '{parameters[i].ParameterType.Name}' for '{type.Name}'.");
                if (args[i] == null)
                    args[i] = parameters[i].DefaultValue;
            }

            return ctor.Invoke(args);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var disposable in _disposables)
                {
                    try { disposable.Dispose(); } catch { }
                }
                _disposables.Clear();
                _scopedInstances.Clear();
            }
        }
    }
}
