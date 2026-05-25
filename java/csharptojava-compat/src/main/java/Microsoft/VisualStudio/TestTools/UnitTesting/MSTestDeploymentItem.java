package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.lang.annotation.ElementType;
import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares a deployment item for an MSTest test class or method.
 * The source path is resolved against classpath resources first,
 * then the module working directory.
 */
@Target({ElementType.TYPE, ElementType.METHOD})
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(MSTestDeploymentItems.class)
public @interface MSTestDeploymentItem {
    /** Source path relative to the project or resources directory. */
    String source();

    /** Target subdirectory under the deployment directory. Empty means deploy to deployment root. */
    String outputDirectory() default "";
}
