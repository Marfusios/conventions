using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GraphQL.Conventions.Adapters;
using GraphQL.Conventions.Adapters.Engine.Listeners.DataLoader;
using GraphQL.Conventions.Builders;
using GraphQL.Conventions.Execution;
using GraphQL.Conventions.Types.Descriptors;
using GraphQL.Conventions.Types.Resolution;
using GraphQL.Execution;
using GraphQL.Http;
using GraphQL.Instrumentation;
using GraphQL.Types;
using GraphQL.Utilities;
using GraphQL.Validation;

namespace GraphQL.Conventions
{
    public class GraphQLEngine
    {
        private readonly TypeResolver _typeResolver = new TypeResolver();

        private readonly GraphTypeAdapter _graphTypeAdapter = new GraphTypeAdapter();

        private readonly SchemaConstructor<ISchema, IGraphType> _constructor;

        private readonly DocumentExecuter _documentExecutor = new DocumentExecuter();

        private readonly IDocumentBuilder _documentBuilder = new GraphQLDocumentBuilder();

        private readonly DocumentValidator _documentValidator = new DocumentValidator();

        private readonly DocumentWriter _documentWriter = new DocumentWriter();

        private SchemaPrinter _schemaPrinter;

        private ISchema _schema;

        private List<System.Type> _schemaTypes = new List<System.Type>();

        private class NoopValidationRule : IValidationRule
        {
            public INodeVisitor Validate(ValidationContext context)
            {
                return new EnterLeaveListener(_ => { });
            }
        }

        private class WrappedDependencyInjector : IDependencyInjector
        {
            private readonly GraphQLEngine _engine;

            private readonly Func<System.Type, object> _typeResolutionDelegate;

            public WrappedDependencyInjector(GraphQLEngine engine, Func<System.Type, object> typeResolutionDelegate)
            {
                _engine = engine;
                _typeResolutionDelegate = typeResolutionDelegate;
            }

            public object Resolve(System.Reflection.TypeInfo typeInfo)
            {
                return _typeResolutionDelegate(typeInfo.AsType()) ?? _engine.CreateInstance(typeInfo.AsType());
            }
        }

        public GraphQLEngine(Func<System.Type, object> typeResolutionDelegate = null)
        {
            _constructor = new SchemaConstructor<ISchema, IGraphType>(_graphTypeAdapter, _typeResolver);
            if (typeResolutionDelegate != null)
            {
                _typeResolver.DependencyInjector = new WrappedDependencyInjector(this, typeResolutionDelegate);
                _constructor.TypeResolutionDelegate = type => typeResolutionDelegate(type) ?? CreateInstance(type);
            }
            else
            {
                _constructor.TypeResolutionDelegate = type =>
                    _typeResolver?.DependencyInjector?.Resolve(type.GetTypeInfo()) ?? CreateInstance(type);
            }
        }

        public static GraphQLEngine New(Func<System.Type, object> typeResolutionDelegate = null)
        {
            return new GraphQLEngine(typeResolutionDelegate);
        }

        public static GraphQLEngine New<TQuery>(Func<System.Type, object> typeResolutionDelegate = null)
        {
            return New(typeResolutionDelegate)
                .WithQuery<TQuery>()
                .BuildSchema();
        }

        public static GraphQLEngine New<TQuery, TMutation>(Func<System.Type, object> typeResolutionDelegate = null)
        {
            return New(typeResolutionDelegate)
                .WithQueryAndMutation<TQuery, TMutation>()
                .BuildSchema();
        }

        public GraphQLEngine WithFieldResolutionStrategy(FieldResolutionStrategy strategy)
        {
            switch (strategy)
            {
                default:
                case FieldResolutionStrategy.Normal:
                    _graphTypeAdapter.FieldResolverFactory = (FieldInfo) => new FieldResolver(FieldInfo);
                    break;
                case FieldResolutionStrategy.WrappedAsynchronous:
                    _graphTypeAdapter.FieldResolverFactory = (FieldInfo) => new WrappedAsyncFieldResolver(FieldInfo);
                    break;
                case FieldResolutionStrategy.WrappedSynchronous:
                    _graphTypeAdapter.FieldResolverFactory = (FieldInfo) => new WrappedSyncFieldResolver(FieldInfo);
                    break;
            }
            return this;
        }

        public GraphQLEngine WithQuery<TQuery>()
        {
            _schemaTypes.Add(typeof(SchemaDefinition<TQuery>));
            return this;
        }

        public GraphQLEngine WithMutation<TMutation>()
        {
            _schemaTypes.Add(typeof(SchemaDefinitionWithMutation<TMutation>));
            return this;
        }

        public GraphQLEngine WithQueryAndMutation<TQuery, TMutation>()
        {
            _schemaTypes.Add(typeof(SchemaDefinition<TQuery, TMutation>));
            return this;
        }

        public GraphQLEngine WithSubscription<TSubscription>()
        {
            _schemaTypes.Add(typeof(SchemaDefinitionWithSubscription<TSubscription>));
            return this;
        }

        public GraphQLEngine WithAttributesFromAssembly(System.Type assemblyType)
        {
            _typeResolver.RegisterAttributesInAssembly(assemblyType);
            return this;
        }

        public GraphQLEngine WithAttributesFromAssembly<TAssemblyType>()
        {
            return WithAttributesFromAssembly(typeof(TAssemblyType));
        }

        public GraphQLEngine WithAttributesFromAssemblies(IEnumerable<System.Type> assemblyTypes)
        {
            foreach (var assemblyType in assemblyTypes)
            {
                WithAttributesFromAssembly(assemblyType);
            }
            return this;
        }

        public GraphQLEngine BuildSchema(params System.Type[] types)
        {
            if (_schema == null)
            {
                if (types.Length > 0)
                {
                    _schemaTypes.AddRange(types);
                }
                _schema = _constructor.Build(_schemaTypes.ToArray());
                _schemaPrinter = new SchemaPrinter(_schema, new[] { TypeNames.Url, TypeNames.Uri, TypeNames.TimeSpan, TypeNames.Guid });
            }
            return this;
        }

        public string Describe()
        {
            BuildSchema(); // Ensure that the schema has been constructed
            return _schemaPrinter.Print();
        }

        public GraphQLExecutor NewExecutor(IRequestDeserializer requestDeserializer = null)
        {
            BuildSchema(); // Ensure that the schema has been constructed
            return new GraphQLExecutor(this, requestDeserializer ?? new RequestDeserializer());
        }

        public GraphQLEngine RegisterScalarType<TType, TGraphType>(string name = null)
        {
            _typeResolver.RegisterScalarType<TType>(name ?? typeof(TType).Name);
            _graphTypeAdapter.RegisterScalarType<TGraphType>(name ?? typeof(TType).Name);
            return this;
        }

        public string SerializeResult(ExecutionResult result)
        {
            return _documentWriter.Write(result);
        }

        internal IDependencyInjector DependencyInjector
        {
            get { return _typeResolver.DependencyInjector; }
            set { _typeResolver.DependencyInjector = value; }
        }

        internal async Task<ExecutionResult> Execute(
            object rootObject,
            string query,
            string operationName,
            Inputs inputs,
            IUserContext userContext,
            bool enableValidation = true,
            bool enableProfiling = false,
            IEnumerable<IValidationRule> rules = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!enableValidation)
            {
                rules = new[] { new NoopValidationRule() };
            }
            var configuration = new ExecutionOptions
            {
                Schema = _schema,
                Root = rootObject,
                Query = query,
                OperationName = operationName,
                Inputs = inputs,
                UserContext = userContext,
                ValidationRules = rules != null && rules.Any() ? rules : null,
                CancellationToken = cancellationToken,
            };

            if (userContext is IDataLoaderContextProvider)
            {
                configuration.Listeners.Add(new DataLoaderListener());
            }

            if (enableProfiling)
            {
                configuration.FieldMiddleware.Use<InstrumentFieldsMiddleware>();
            }

            var result = await _documentExecutor.ExecuteAsync(configuration).ConfigureAwait(false);

            if (result.Errors != null)
            {
                var errors = new ExecutionErrors();
                foreach (var executionError in result.Errors)
                {
                    var exception = new FieldResolutionException(executionError);
                    var error = new ExecutionError(exception.Message, exception);
                    error.SetCode(exception.InnerException ?? exception);
                    foreach (var location in executionError.Locations ?? new ErrorLocation[0])
                    {
                        error.AddLocation(location.Line, location.Column);
                    }
                    error.Path.AddRange(executionError.Path);
                    errors.Add(error);
                }
                result.Errors = errors;
            }

            return result;
        }

        internal IValidationResult Validate(string queryString)
        {
            var document = _documentBuilder.Build(queryString);
            return _documentValidator.Validate(queryString, _schema, document);
        }

        private object CreateInstance(System.Type type)
        {
            var typeInfo = type.GetTypeInfo();
            if (!typeInfo.IsAbstract && !typeInfo.ContainsGenericParameters)
            {
                var ctors = typeInfo.GetConstructors().ToList();
                if (ctors.All(ctor => ctor.GetParameters().Any()))
                {
                    var ctor = ctors.FirstOrDefault();
                    if (ctor == null)
                    {
                        return null;
                    }
                    var parameters = ctor.GetParameters();
                    var parameterValues = parameters
                        .Select(parameter => _constructor.TypeResolutionDelegate(parameter.ParameterType))
                        .ToArray();
                    return ctor.Invoke(parameterValues);
                }
            }

            return Activator.CreateInstance(type);
        }
    }
}
