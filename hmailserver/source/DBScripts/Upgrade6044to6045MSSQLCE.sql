create table hm_remotedomainpolicies
(
	policyid int identity(1,1) not null,
	policydomainname nvarchar(255) not null,
	policydescription nvarchar(255) not null,
	policyactive tinyint not null,
	policyoutboundtls int not null,
	policyinboundtls tinyint not null,
	policymaxmessagesizekb int not null,
	policymaxconnections int not null,
	policymaxperminute int not null,
	policyallowreplies tinyint not null,
	policyallowforwarding tinyint not null,
	policycalloutenabled tinyint not null,
	policycallouthost nvarchar(255) not null,
	policycalloutport int not null,
	policycallouttimeout int not null,
	policycalloutcacheminutes int not null,
	policycalloutperminute int not null
)

ALTER TABLE hm_remotedomainpolicies ADD CONSTRAINT hm_remotedomainpolicies_pk PRIMARY KEY (policyid)

CREATE INDEX idx_hm_remotedomainpolicies_domain ON hm_remotedomainpolicies (policydomainname)

update hm_dbversion set value = 6045
