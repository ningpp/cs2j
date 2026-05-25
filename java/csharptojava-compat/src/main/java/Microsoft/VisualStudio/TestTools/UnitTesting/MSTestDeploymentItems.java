package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Container annotation for repeated {@link MSTestDeploymentItem} annotations.
 */
@Target({ElementType.TYPE, ElementType.METHOD})
@Retention(RetentionPolicy.RUNTIME)
public @interface MSTestDeploymentItems {
    MSTestDeploymentItem[] value();
}
