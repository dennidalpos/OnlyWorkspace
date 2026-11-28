@{
    # Correctness checks only: no wholesale style changes or changes to signing interfaces.
    IncludeRules = @(
        'PSAvoidAssignmentToAutomaticVariable'
        'PSAvoidUsingInvokeExpression'
        'PSAvoidUsingUsernameAndPasswordParams'
        'PSReservedParams'
        'PSPossibleIncorrectComparisonWithNull'
        'PSPossibleIncorrectUsageOfRedirectionOperator'
        'PSPossibleIncorrectUsageOfAssignmentOperator'
        'PSMisleadingBacktick'
        'PSAvoidDefaultValueSwitchParameter'
        'PSAvoidUsingBrokenHashAlgorithms'
    )
}
