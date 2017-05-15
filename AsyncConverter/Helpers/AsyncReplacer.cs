using System.Linq;
using AsyncConverter.AsyncHelpers.AwaitElider;
using AsyncConverter.AsyncHelpers.LastNodeChecker;
using JetBrains.Annotations;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;

namespace AsyncConverter.Helpers
{
    [SolutionComponent]
    public class AsyncReplacer : IAsyncReplacer
    {
        private readonly IAsyncInvocationReplacer asyncInvocationReplacer;
        private readonly IInvocationConverter invocationConverter;
        private readonly IAwaitElider awaitElider;
        private readonly ILastNodeChecker lastNodeChecker;

        public AsyncReplacer(IAsyncInvocationReplacer asyncInvocationReplacer, IInvocationConverter invocationConverter, IAwaitElider awaitElider, ILastNodeChecker lastNodeChecker)
        {
            this.asyncInvocationReplacer = asyncInvocationReplacer;
            this.invocationConverter = invocationConverter;
            this.awaitElider = awaitElider;
            this.lastNodeChecker = lastNodeChecker;
        }

        public void ReplaceToAsync(IMethod method)
        {
            foreach (var methodDeclaration in method
                .FindAllHierarchy()
                .SelectMany(x => x.GetDeclarations<IMethodDeclaration>()))
            {
                ReplaceMethodToAsync(methodDeclaration);
            }
        }

        private void ReplaceMethodToAsync(IMethodDeclaration method)
        {
            if (!method.IsValid())
                return;

            var methodDeclaredElement = method.DeclaredElement;
            if (methodDeclaredElement == null)
                return;

            var finder = method.GetPsiServices().Finder;
            var usages = finder.FindAllReferences(methodDeclaredElement);
            foreach (var usage in usages)
            {
                var invocation = usage.GetTreeNode().Parent as IInvocationExpression;
                asyncInvocationReplacer.ReplaceInvocation(invocation, GenerateAsyncMethodName(method.DeclaredName), invocation?.IsUnderAsyncDeclaration() ?? false);
            }

            //TODO: ugly hack. think
            while (true)
            {
                var allInvocationReplaced = method
                    .Body
                    .Descendants<IInvocationExpression>()
                    .ToEnumerable()
                    .All(invocationExpression => !invocationConverter.TryReplaceInvocationToAsync(invocationExpression));
                if(allInvocationReplaced)
                    break;
            }

            ReplaceMethodSignatureToAsync(methodDeclaredElement, method);
        }

        private string GenerateAsyncMethodName([NotNull] string oldName) => oldName.EndsWith("Async") ? oldName : $"{oldName}Async";

        private void ReplaceMethodSignatureToAsync([NotNull] IParametersOwner parametersOwner, [NotNull] IMethodDeclaration methodDeclaration)
        {
            var returnType = parametersOwner.ReturnType;

            var psiModule = methodDeclaration.GetPsiModule();
            IDeclaredType newReturnValue;
            if (returnType.IsVoid())
            {
                newReturnValue = TypeFactory.CreateTypeByCLRName("System.Threading.Tasks.Task", psiModule);
            }
            else
            {
                var task = TypeFactory.CreateTypeByCLRName("System.Threading.Tasks.Task`1", psiModule).GetTypeElement();
                if (task == null)
                    return;
                newReturnValue = TypeFactory.CreateType(task, returnType);
            }

            var name = GenerateAsyncMethodName(methodDeclaration.DeclaredName);

            SetSignature(methodDeclaration, newReturnValue, name);

            //TODO: encapsulate this
            var awaitExpressions = methodDeclaration.DescendantsInScope<IAwaitExpression>().ToArray();
            if(awaitExpressions.Length == 1 && lastNodeChecker.IsLastNode(awaitExpressions.First()))
                awaitElider.Elide(awaitExpressions.First());
        }

        private static void SetSignature([NotNull] IMethodDeclaration methodDeclaration, [NotNull] IType newReturnValue, [NotNull] string newName)
        {
            methodDeclaration.SetType(newReturnValue);
            if(!methodDeclaration.IsAbstract)
                methodDeclaration.SetAsync(true);
            methodDeclaration.SetName(newName);
        }
    }
}